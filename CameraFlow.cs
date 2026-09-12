using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using WinRT;

namespace WindowsDuo;

/// <summary>
/// Global vertical shift of a still scene. Laptop-lid close pitches the webcam
/// down, so the whole picture slides toward the top of the frame.
/// </summary>
internal sealed class CameraFlow : IDisposable
{
    private const int ProfileHeight = 90;
    private const int MaxLag = 14;

    private readonly object _gate = new();
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private float[]? _prev;
    private float[] _curr = new float[ProfileHeight];
    private bool _busy;
    private bool _disposed;
    private int _frameErrors;
    private static readonly Guid MemoryBufferIid = new("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");

    /// <summary>Positive = content moved toward the top of the frame (lid closing).</summary>
    public event Action<float, float>? Shift;

    public bool Running => _reader is not null;

    public async Task StartAsync()
    {
        if (_reader is not null)
        {
            return;
        }

        var groups = await MediaFrameSourceGroup.FindAllAsync();
        var group = groups.FirstOrDefault(g =>
            g.SourceInfos.Any(s => s.SourceKind == MediaFrameSourceKind.Color));
        if (group is null)
        {
            throw new InvalidOperationException("没有可用的摄像头。");
        }

        var capture = new MediaCapture();
        await capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            SourceGroup = group,
            SharingMode = MediaCaptureSharingMode.SharedReadOnly,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
        });

        var source = capture.FrameSources.Values.FirstOrDefault(s =>
            s.Info.SourceKind == MediaFrameSourceKind.Color)
            ?? throw new InvalidOperationException("摄像头没有彩色画面。");

        MediaFrameReader reader;
        try
        {
            reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
        }
        catch
        {
            reader = await capture.CreateFrameReaderAsync(source);
        }

        reader.FrameArrived += OnFrame;
        var status = await reader.StartAsync();
        if (status != MediaFrameReaderStartStatus.Success)
        {
            reader.FrameArrived -= OnFrame;
            reader.Dispose();
            capture.Dispose();
            throw new InvalidOperationException("摄像头启动失败: " + status);
        }

        _capture = capture;
        _reader = reader;
        Log.Info($"camera flow start group={group.DisplayName}");
    }

    public async Task StopAsync()
    {
        var reader = _reader;
        var capture = _capture;
        _reader = null;
        _capture = null;
        lock (_gate)
        {
            _prev = null;
        }

        _frameErrors = 0;

        if (reader is not null)
        {
            reader.FrameArrived -= OnFrame;
            try
            {
                await reader.StopAsync();
            }
            catch
            {
            }

            reader.Dispose();
        }

        capture?.Dispose();
        Log.Info("camera flow stop");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = StopAsync();
    }

    private void OnFrame(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (Interlocked.Exchange(ref _busy, true))
        {
            return;
        }

        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap is null)
            {
                return;
            }

            using var bgra = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                ? SoftwareBitmap.Copy(bitmap)
                : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8);

            if (!TryProfile(bgra, _curr, out var width, out var height))
            {
                return;
            }

            if (_frameErrors != -1)
            {
                _frameErrors = -1;
                Log.Info($"camera pixels {width}x{height}");
            }

            float lag;
            float ncc;
            lock (_gate)
            {
                if (_prev is null)
                {
                    _prev = (float[])_curr.Clone();
                    return;
                }

                lag = Match(_prev, _curr, out ncc);
                Array.Copy(_curr, _prev, _curr.Length);
            }

            if (ncc < 0.68f)
            {
                return;
            }

            Shift?.Invoke(lag, ncc);
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref _frameErrors) <= 3)
            {
                Log.Warn("camera frame: " + Log.Describe(ex));
            }
        }
        finally
        {
            Volatile.Write(ref _busy, false);
        }
    }

    private static unsafe bool TryProfile(SoftwareBitmap bitmap, float[] profile, out int srcW, out int srcH)
    {
        srcW = bitmap.PixelWidth;
        srcH = bitmap.PixelHeight;
        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        using var reference = buffer.CreateReference();
        var plane = buffer.GetPlaneDescription(0);
        srcW = plane.Width;
        srcH = plane.Height;
        if (srcW < 8 || srcH < 8)
        {
            return false;
        }

        var unknown = ((IWinRTObject)reference).NativeObject.ThisPtr;
        var iid = MemoryBufferIid;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var access));
        try
        {
            var vtable = *(nint**)access;
            var getBuffer = (delegate* unmanaged[Stdcall]<nint, byte**, uint*, int>)vtable[3];
            byte* data;
            uint capacity;
            Marshal.ThrowExceptionForHR(getBuffer(access, &data, &capacity));
            if (plane.StartIndex + (plane.Height - 1) * plane.Stride + plane.Width * 4 > capacity)
            {
                return false;
            }

            Array.Clear(profile);
            var counts = (stackalloc int[ProfileHeight]);
            for (var y = 0; y < srcH; y++)
            {
                var dy = Math.Clamp(y * ProfileHeight / srcH, 0, ProfileHeight - 1);
                var row = data + plane.StartIndex + y * plane.Stride;
                for (var x = 0; x < srcW; x++)
                {
                    var p = row + x * 4;
                    profile[dy] += (p[2] * 77 + p[1] * 150 + p[0] * 29) >> 8;
                    counts[dy]++;
                }
            }

            for (var i = 0; i < ProfileHeight; i++)
            {
                profile[i] = counts[i] > 0 ? profile[i] / counts[i] : 0;
            }

            return true;
        }
        finally
        {
            Marshal.Release(access);
        }
    }

    /// <summary>
    /// Lag that aligns current[i+lag] to previous[i]. Positive lag means the
    /// new frame's content sits higher (lid closing).
    /// </summary>
    private static float Match(float[] prev, float[] curr, out float bestNcc)
    {
        var n = prev.Length;
        var meanP = 0f;
        var meanC = 0f;
        for (var i = 0; i < n; i++)
        {
            meanP += prev[i];
            meanC += curr[i];
        }

        meanP /= n;
        meanC /= n;

        bestNcc = float.MinValue;
        var bestLag = 0;
        var scores = (stackalloc float[MaxLag * 2 + 1]);
        for (var lag = -MaxLag; lag <= MaxLag; lag++)
        {
            var dot = 0f;
            var vP = 0f;
            var vC = 0f;
            var i0 = Math.Max(0, -lag);
            var i1 = Math.Min(n, n - lag);
            for (var i = i0; i < i1; i++)
            {
                var a = prev[i] - meanP;
                var b = curr[i + lag] - meanC;
                dot += a * b;
                vP += a * a;
                vC += b * b;
            }

            var ncc = vP > 1e-3f && vC > 1e-3f ? dot / MathF.Sqrt(vP * vC) : 0f;
            scores[lag + MaxLag] = ncc;
            if (ncc > bestNcc)
            {
                bestNcc = ncc;
                bestLag = lag;
            }
        }

        var shift = (float)bestLag;
        if (Math.Abs(bestLag) >= MaxLag)
        {
            bestNcc = 0f;
            return 0f;
        }

        var idx = bestLag + MaxLag;
        if (idx > 0 && idx < scores.Length - 1)
        {
            var a = scores[idx - 1];
            var b = scores[idx];
            var c = scores[idx + 1];
            var denom = a - 2f * b + c;
            if (MathF.Abs(denom) > 1e-5f)
            {
                shift += 0.5f * (a - c) / denom;
            }
        }

        return shift;
    }
}

