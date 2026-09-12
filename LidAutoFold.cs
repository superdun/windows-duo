using System.Windows.Threading;
using Windows.Devices.Sensors;

namespace WindowsDuo;

internal sealed class LidAutoFold : IDisposable
{
    private const float MaxFold = 80f;
    private const float FlowSign = -1f;
    private const float BaseDegreesPerPixel = 0.55f;
    private const float StillFlow = 0.18f;
    private const float Deadzone = 0.16f;
    private const float TrackSmooth = 0.12f;
    private const float FadeSmooth = 1.05f;
    private const int ConfirmFrames = 4;

    private static readonly TimeSpan KeyboardGate = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CameraWarm = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan GpuWarm = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan StillHold = TimeSpan.FromSeconds(10);

    private readonly GpuDesktopFilter _filter;
    private readonly Dispatcher _ui;
    private readonly KeyboardIdle _keys = new();
    private readonly DispatcherTimer _tick;
    private readonly CameraFlow _camera = new();
    private readonly object _gate = new();

    private Phase _phase = Phase.Idle;
    private float _fold;
    private int _closeStreak;
    private long _lastMotionMs;
    private bool _wantGpu;
    private bool _disposed;
    private HingeAngleSensor? _hinge;
    private Inclinometer? _inclinometer;
    private double? _lastAngle;
    private bool _cameraBusy;
    private float _sensitivity = 1.25f;

    public float Sensitivity
    {
        get => Volatile.Read(ref _sensitivity);
        set => Volatile.Write(ref _sensitivity, Math.Clamp(value, 0.15f, 3f));
    }

    public LidAutoFold(GpuDesktopFilter filter, Dispatcher ui)
    {
        _filter = filter;
        _ui = ui;
        _camera.Shift += OnCameraShift;
        _tick = new DispatcherTimer(DispatcherPriority.Background, ui)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _tick.Tick += (_, _) => Pump();
    }

    public LidHardwareReport Hardware { get; private set; }
    public string Source { get; private set; } = "未探测";
    public string Status { get; private set; } = "正在检查传感器…";
    public bool Enabled { get; private set; } = true;
    public bool Driving { get; private set; }
    public bool ManualPreview { get; set; }

    public event Action? Changed;

    public async Task StartAsync()
    {
        Hardware = await LidHardware.ProbeAsync();
        if (Hardware.Hinge)
        {
            try
            {
                _hinge = await HingeAngleSensor.GetDefaultAsync();
                if (_hinge is not null)
                {
                    _hinge.ReadingChanged += OnHinge;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("hinge subscribe: " + Log.Describe(ex));
            }
        }

        if (_hinge is null && Hardware.Inclinometer)
        {
            _inclinometer = Inclinometer.GetDefault();
            if (_inclinometer is not null)
            {
                _inclinometer.ReadingChanged += OnIncline;
            }
        }

        Source = _hinge is not null
            ? "铰链角"
            : _inclinometer is not null
                ? "倾角计"
                : string.IsNullOrEmpty(Hardware.CameraName)
                    ? "无"
                    : "摄像头光流";
        Status = Hardware.Summary;
        _keys.Install();
        _tick.Start();
        Raise();
        Log.Info($"lid auto source={Source}");
    }

    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        if (!enabled)
        {
            CancelToIdle();
        }

        Raise();
    }

    public void CancelToIdle()
    {
        lock (_gate)
        {
            _phase = Phase.Idle;
            _fold = 0;
            _closeStreak = 0;
            _wantGpu = false;
            Driving = false;
        }

        _filter.ClearAutoFold();
        _ui.InvokeAsync(StopGpuIfAuto);
        _ = StopCameraAsync();
        Status = Enabled ? Hardware.Summary : "自动跟盖已关闭";
        Raise();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tick.Stop();
        _keys.Dispose();
        _camera.Shift -= OnCameraShift;
        if (_hinge is not null)
        {
            _hinge.ReadingChanged -= OnHinge;
        }

        if (_inclinometer is not null)
        {
            _inclinometer.ReadingChanged -= OnIncline;
        }

        try
        {
            _camera.Dispose();
        }
        catch
        {
        }
    }

    private void Pump()
    {
        if (!Enabled)
        {
            return;
        }

        var idle = _keys.Idle;
        Phase phase;
        bool wantGpu;
        lock (_gate)
        {
            phase = _phase;
            wantGpu = _wantGpu;
            if (idle < KeyboardGate && phase is Phase.Watching or Phase.Idle)
            {
                _closeStreak = 0;
                _lastAngle = null;
            }

            if (idle < TimeSpan.FromMilliseconds(400) && phase is Phase.Tracking or Phase.Fading)
            {
                BeginFade();
                phase = _phase;
                wantGpu = _wantGpu;
            }
        }

        if (idle >= CameraWarm && _hinge is null && _inclinometer is null)
        {
            _ = EnsureCameraAsync();
        }
        if (idle < CameraWarm)
        {
            Phase p;
            lock (_gate)
            {
                p = _phase;
            }

            if (p is not Phase.Tracking and not Phase.Fading)
            {
                _ = StopCameraAsync();
            }
        }

        if (idle >= CameraWarm)
        {
            lock (_gate)
            {
                if (_phase == Phase.Idle)
                {
                    _phase = Phase.Watching;
                    Status = idle >= KeyboardGate
                        ? $"{Source} · 等待盖子下翻"
                        : $"{Source} · 摄像头准备中";
                }
                else if (_phase == Phase.Watching && idle >= KeyboardGate && !Status.Contains("等待盖子", StringComparison.Ordinal))
                {
                    Status = $"{Source} · 等待盖子下翻";
                }
            }
        }
        else if (idle < CameraWarm)
        {
            lock (_gate)
            {
                if (_phase == Phase.Watching && !Driving)
                {
                    _phase = Phase.Idle;
                    Status = Hardware.Summary;
                }
            }
        }

        if (idle >= GpuWarm && !ManualPreview && phase is Phase.Watching or Phase.Tracking or Phase.Fading)
        {
            lock (_gate)
            {
                _wantGpu = true;
                wantGpu = true;
            }
        }

        if (idle < GpuWarm && phase is Phase.Idle or Phase.Watching && !Driving)
        {
            wantGpu = false;
            lock (_gate)
            {
                _wantGpu = false;
            }
        }

        if (wantGpu && !ManualPreview && !_filter.Running)
        {
            try
            {
                _filter.SnapShown(0);
                _filter.SetAutoFold(0, TrackSmooth);
                _filter.Start();
            }
            catch (Exception ex)
            {
                Log.Error("auto overlay start", ex);
            }
        }
        else if (!wantGpu && _filter.Running && !ManualPreview && !Driving)
        {
            StopGpuIfAuto();
        }

        if (phase == Phase.Fading && _filter.ShownAmount < 0.18f)
        {
            lock (_gate)
            {
                _phase = idle >= KeyboardGate ? Phase.Watching : Phase.Idle;
                _fold = 0;
                Driving = false;
                _wantGpu = idle >= GpuWarm;
            }

            _filter.ClearAutoFold();
            if (!ManualPreview && idle < GpuWarm)
            {
                StopGpuIfAuto();
            }

            Status = idle >= KeyboardGate ? $"{Source} · 等待盖子下翻" : Hardware.Summary;
        }
    }

    private void OnCameraShift(float lag, float ncc)
    {
        // Negative lag (picture slides down) is closing after FlowSign.
        if (MathF.Abs(lag) < Deadzone)
        {
            lag = 0f;
        }

        ApplyCloseDelta(FlowSign * lag * BaseDegreesPerPixel * Sensitivity, MathF.Abs(lag), ncc);
    }

    private void OnHinge(HingeAngleSensor sender, HingeAngleSensorReadingChangedEventArgs args)
    {
        var angle = args.Reading.AngleInDegrees;
        ApplyAbsoluteAngle(angle, closingIfDecreasing: true);
    }

    private void OnIncline(Inclinometer sender, InclinometerReadingChangedEventArgs args)
    {
        ApplyAbsoluteAngle(args.Reading.PitchDegrees, closingIfDecreasing: false);
    }

    private void ApplyAbsoluteAngle(double angle, bool closingIfDecreasing)
    {
        double delta;
        lock (_gate)
        {
            if (_lastAngle is null)
            {
                _lastAngle = angle;
                return;
            }

            delta = closingIfDecreasing ? _lastAngle.Value - angle : angle - _lastAngle.Value;
            _lastAngle = angle;
        }

        ApplyCloseDelta((float)delta, (float)Math.Abs(delta), 1f);
    }

    private void ApplyCloseDelta(float closeDegrees, float magnitude, float confidence)
    {
        if (!Enabled || ManualPreview || confidence < 0.68f)
        {
            return;
        }

        var limit = Math.Clamp(0.7f * Sensitivity, 0.35f, 2.2f);
        closeDegrees = Math.Clamp(closeDegrees, -limit, limit);
        var closeTrigger = 0.12f / Math.Max(Sensitivity, 0.4f);
        var now = Environment.TickCount64;
        var idle = _keys.Idle >= KeyboardGate;
        var trackingSmooth = TrackSmooth;

        lock (_gate)
        {
            switch (_phase)
            {
                case Phase.Idle:
                    break;
                case Phase.Watching:
                    if (!idle)
                    {
                        _closeStreak = 0;
                        break;
                    }

                    if (closeDegrees > closeTrigger)
                    {
                        _closeStreak++;
                    }
                    else
                    {
                        _closeStreak = 0;
                    }

                    if (_closeStreak >= ConfirmFrames)
                    {
                        _phase = Phase.Tracking;
                        _fold = 0;
                        Driving = true;
                        _wantGpu = true;
                        _lastMotionMs = now;
                        _closeStreak = 0;
                        Status = $"{Source} · 跟盖 {0:0}°";
                        Log.Info($"lid track start via {Source}");
                    }

                    break;
                case Phase.Tracking:
                    _fold = Math.Clamp(_fold + closeDegrees, 0f, MaxFold);
                    if (magnitude > StillFlow)
                    {
                        _lastMotionMs = now;
                    }

                    Status = $"{Source} · 跟盖 {_fold:0}°";
                    if (now - _lastMotionMs >= (long)StillHold.TotalMilliseconds)
                    {
                        BeginFade();
                    }

                    break;
                case Phase.Fading:
                    if (idle && closeDegrees > closeTrigger)
                    {
                        _phase = Phase.Tracking;
                        _fold = Math.Clamp(_filter.ShownAmount, 0f, MaxFold);
                        Driving = true;
                        _wantGpu = true;
                        _lastMotionMs = now;
                        Status = $"{Source} · 跟盖 {_fold:0}°";
                    }

                    break;
            }

            trackingSmooth = _phase == Phase.Fading ? FadeSmooth : TrackSmooth;
            if (_phase is Phase.Tracking || _phase == Phase.Fading && Driving)
            {
                var target = _phase == Phase.Fading ? 0f : _fold;
                _filter.SetAutoFold(target, trackingSmooth);
            }
        }

        Raise();
    }

    private void BeginFade()
    {
        if (_phase == Phase.Fading)
        {
            return;
        }

        _phase = Phase.Fading;
        Driving = true;
        _wantGpu = true;
        Status = $"{Source} · 退模糊";
        _filter.SetAutoFold(0, FadeSmooth);
        Log.Info("lid fade");
    }

    private void StopGpuIfAuto()
    {
        if (ManualPreview)
        {
            return;
        }

        if (_filter.Running)
        {
            _filter.Stop();
        }
    }

    private async Task EnsureCameraAsync()
    {
        if (_camera.Running || _cameraBusy || _hinge is not null || _inclinometer is not null)
        {
            return;
        }

        if (string.IsNullOrEmpty(Hardware.CameraName))
        {
            Status = "没有盖子角度传感器，也没有摄像头";
            return;
        }

        _cameraBusy = true;
        try
        {
            await _camera.StartAsync();
            Status = $"{Source} · 摄像头已就绪";
        }
        catch (Exception ex)
        {
            Log.Error("camera start", ex);
            Status = "摄像头不可用: " + ex.Message;
        }
        finally
        {
            _cameraBusy = false;
            Raise();
        }
    }

    private async Task StopCameraAsync()
    {
        if (!_camera.Running || _cameraBusy)
        {
            return;
        }

        _cameraBusy = true;
        try
        {
            await _camera.StopAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("camera stop: " + Log.Describe(ex));
        }
        finally
        {
            _cameraBusy = false;
        }
    }

    private void Raise() => _ui.BeginInvoke(() => Changed?.Invoke(), DispatcherPriority.Background);

    private enum Phase
    {
        Idle,
        Watching,
        Tracking,
        Fading,
    }
}
