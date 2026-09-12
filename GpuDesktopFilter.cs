using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Result = SharpGen.Runtime.Result;

namespace WindowsDuo;

internal sealed class GpuDesktopFilter : IDisposable
{
    /// <summary>
    /// Black margin around the picture, matching Mac Duo's 120pt pad so the
    /// Gaussian pyramid reaches real black on every side of the glass.
    /// </summary>
    private const int Padding = 120;
    private static readonly Result DxgiWaitTimeout = unchecked((int)0x887A0027);

    private readonly object _gate = new();
    private EffectParams _params = new();
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private PresentWindow? _window;

    public string AdapterName { get; private set; } = "";
    public double Fps { get; private set; }
    public string? LastError { get; private set; }
    public bool Running => _thread is { IsAlive: true };

    public void UpdateParams(EffectParams effect)
    {
        lock (_gate)
        {
            _params = effect;
        }
    }

    public void Start()
    {
        if (Running)
        {
            return;
        }

        Stop();
        LastError = null;
        Log.Info("filter start");
        _window = new PresentWindow();
        _window.ShowOnPrimary();
        _cts = new CancellationTokenSource();
        var hwnd = _window.Handle;
        var overlay = _window.OverlayBounds;
        var desktop = _window.DesktopBounds;
        var work = _window.WorkingArea;
        var token = _cts.Token;
        _thread = new Thread(() => RenderLoop(hwnd, overlay, desktop, work, token))
        {
            IsBackground = true,
            Name = "WindowsDuo GPU",
        };
        _thread.Start();
    }

    public void Stop()
    {
        if (_cts is null && _thread is null && _window is null)
        {
            return;
        }

        Log.Info("filter stop");
        _cts?.Cancel();
        _thread?.Join(1000);
        _thread = null;
        _cts?.Dispose();
        _cts = null;
        _window?.Dispose();
        _window = null;
    }

    public void Dispose() => Stop();

    private void RenderLoop(
        nint hwnd,
        System.Drawing.Rectangle overlay,
        System.Drawing.Rectangle desktop,
        System.Drawing.Rectangle work,
        CancellationToken token)
    {
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        IDXGIOutputDuplication? duplication = null;
        IDXGISwapChain1? swapChain = null;
        ID3D11RenderTargetView? rtv = null;
        ID3D11Texture2D? picture = null;
        ID3D11ShaderResourceView? pictureSrv = null;
        ID3D11ShaderResourceView[]? mipSrvs = null;
        ID3D11RenderTargetView[]? mipRtvs = null;
        ID3D11VertexShader? vs = null;
        ID3D11PixelShader? ps = null;
        ID3D11PixelShader? downsample = null;
        ID3D11Buffer? cb = null;
        ID3D11SamplerState? linear = null;
        ID3D11SamplerState? point = null;
        ID3D11RasterizerState? raster = null;
        Blob? vsBlob = null;
        Blob? psBlob = null;
        Blob? downBlob = null;

        try
        {
            CreateDeviceForPrimary(hwnd, overlay.Width, overlay.Height, out device, out context, out duplication, out swapChain, out var width, out var height);
            var workLeft = Math.Max(work.X - desktop.X, 0);
            var workTop = Math.Max(work.Y - desktop.Y, 0);
            var workBox = new Box(workLeft, workTop, 0, workLeft + work.Width, workTop + work.Height, 1);
            Log.Info($"device ready adapter={AdapterName} overlay={width}x{height} work={work.Width}x{work.Height} at ({workLeft},{workTop})");
            vsBlob = Compile("VSMain", "vs_5_0");
            psBlob = Compile("PSMain", "ps_5_0");
            downBlob = Compile("PSDownsample", "ps_5_0");
            vs = device.CreateVertexShader(vsBlob);
            ps = device.CreatePixelShader(psBlob);
            downsample = device.CreatePixelShader(downBlob);
            cb = device.CreateBuffer(new BufferDescription
            {
                ByteWidth = (uint)Marshal.SizeOf<FilterConstants>(),
                BindFlags = BindFlags.ConstantBuffer,
                Usage = ResourceUsage.Dynamic,
                CPUAccessFlags = CpuAccessFlags.Write,
            });
            linear = device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                MaxLOD = float.MaxValue,
            });
            point = device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipPoint,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                MaxLOD = float.MaxValue,
            });
            raster = device.CreateRasterizerState(RasterizerDescription.CullNone);

            using (var backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0))
            {
                rtv = device.CreateRenderTargetView(backBuffer);
            }

            var frames = 0;
            var stamp = Environment.TickCount64;
            var warnedAcquire = false;
            var overlayRevealed = false;
            while (!token.IsCancellationRequested)
            {
                var acquired = duplication.AcquireNextFrame(4, out _, out var resource);
                if (acquired.Failure && acquired != DxgiWaitTimeout)
                {
                    if (!warnedAcquire)
                    {
                        Log.Warn($"AcquireNextFrame: {acquired} (0x{(int)acquired:X8})");
                        warnedAcquire = true;
                    }

                    if (picture is null)
                    {
                        LastError = $"AcquireNextFrame: {acquired} (0x{(int)acquired:X8})";
                        break;
                    }
                }

                if (acquired.Success)
                {
                    using (resource)
                    using (var src = resource.QueryInterface<ID3D11Texture2D>())
                    {
                        EnsurePicture(
                            device,
                            context,
                            src.Description.Format,
                            width,
                            height,
                            ref picture,
                            ref pictureSrv,
                            ref mipSrvs,
                            ref mipRtvs);

                        context.ClearState();
                        try
                        {
                            if (!overlayRevealed)
                            {
                                context.CopySubresourceRegion(picture, 0, Padding, Padding, 0, src, 0);
                            }
                            else
                            {
                                context.CopySubresourceRegion(
                                    picture,
                                    0,
                                    (uint)(Padding + workLeft),
                                    (uint)(Padding + workTop),
                                    0,
                                    src,
                                    0,
                                    workBox);
                            }

                            TaskbarCapture.Blit(context, picture!, Padding, desktop);
                        }
                        catch (Exception ex)
                        {
                            LastError = "CopySubresourceRegion: " + Log.Describe(ex);
                            Log.Error($"copy failed dest={picture!.Description.Format} {picture.Description.Width}x{picture.Description.Height} src={src.Description.Width}x{src.Description.Height}", ex);
                            break;
                        }

                        try
                        {
                            BuildGaussianPyramid(context, vs, downsample, cb, point, raster, picture!, mipSrvs!, mipRtvs!);
                        }
                        catch (Exception ex)
                        {
                            LastError = "GaussianPyramid: " + Log.Describe(ex);
                            Log.Error("pyramid failed", ex);
                            break;
                        }
                    }

                    duplication.ReleaseFrame();
                }
                else if (picture is not null && overlayRevealed)
                {
                    context.ClearState();
                    TaskbarCapture.Blit(context, picture, Padding, desktop);
                    BuildGaussianPyramid(context, vs, downsample, cb, point, raster, picture, mipSrvs!, mipRtvs!);
                }

                if (picture is null || pictureSrv is null)
                {
                    continue;
                }

                EffectParams effect;
                lock (_gate)
                {
                    effect = _params;
                }

                const float warpFrom = 40f;
                const float warpUntil = 55f;
                const float fadeUntil = 80f;
                var amount = Math.Clamp(effect.Amount, 0f, fadeUntil);
                var warp = Math.Min(amount, warpUntil);
                var travel = warp * (warpFrom / warpUntil);
                var corners = DepthMath.Corners(width, height, travel, effect.Angle, viewingDistance: 6.0, recession: 1.0);
                var inverse = DepthMath.PictureToScreen(width, height, corners).Inverse();
                var progress = Math.Clamp(travel / 60f, 0f, 1f);
                var blurStrength = MathF.Pow(progress, 1.6f);
                var dimStrength = MathF.Pow(progress, 0.7f);
                var blackoutT = amount <= warpUntil ? 0f : Math.Clamp((amount - warpUntil) / (fadeUntil - warpUntil), 0f, 1f);
                var blackout = blackoutT * blackoutT * (3f - 2f * blackoutT);
                var maxRadius = 10f + effect.Highlight * 150f;
                var paddedW = width + 2 * Padding;
                var paddedH = height + 2 * Padding;
                var maxLevel = (float)Math.Floor(Math.Log2(Math.Max(paddedW, paddedH)));

                WriteConstants(context, cb, new FilterConstants
                {
                    Col0 = new(inverse.M00, inverse.M10, inverse.M20, blackout),
                    Col1 = new(inverse.M01, inverse.M11, inverse.M21, 0),
                    Col2 = new(inverse.M02, inverse.M12, inverse.M22, 0),
                    ScreenAndOrigin = new(width, height, -Padding, -Padding),
                    PaddedAndBlur = new(paddedW, paddedH, maxRadius, blurStrength),
                    Shape = new(effect.Gradient, effect.Sheen, 1f, maxLevel),
                    Light = new(0.2f, dimStrength, 0.5f, effect.Angle > 45f ? 1f : 0f),
                });

                context.ClearRenderTargetView(rtv, new Color4(0, 0, 0, 1));
                context.RSSetViewport(new Viewport(0, 0, width, height));
                context.RSSetState(raster);
                context.OMSetRenderTargets(rtv);
                context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                context.VSSetShader(vs);
                context.PSSetShader(ps);
                context.PSSetConstantBuffer(0, cb);
                context.PSSetShaderResource(0, pictureSrv!);
                context.PSSetSampler(0, linear);
                context.Draw(3, 0);
                swapChain.Present(1, PresentFlags.None);

                frames++;
                if (frames == 1)
                {
                    Log.Info($"first present {width}x{height}");
                    _window?.Reveal();
                    overlayRevealed = true;
                }
                var now = Environment.TickCount64;
                if (now - stamp >= 500)
                {
                    Fps = frames * 1000.0 / (now - stamp);
                    frames = 0;
                    stamp = now;
                }
            }
        }
        catch (Exception ex)
        {
            LastError = Log.Describe(ex);
            Log.Error("render loop failed", ex);
        }
        finally
        {
            context?.ClearState();
            DisposeMips(ref mipSrvs, ref mipRtvs);
            linear?.Dispose();
            point?.Dispose();
            raster?.Dispose();
            cb?.Dispose();
            vs?.Dispose();
            ps?.Dispose();
            downsample?.Dispose();
            vsBlob?.Dispose();
            psBlob?.Dispose();
            downBlob?.Dispose();
            pictureSrv?.Dispose();
            picture?.Dispose();
            rtv?.Dispose();
            swapChain?.Dispose();
            duplication?.Dispose();
            context?.Dispose();
            device?.Dispose();
        }
    }

    private static void EnsurePicture(
        ID3D11Device device,
        ID3D11DeviceContext context,
        Format format,
        int width,
        int height,
        ref ID3D11Texture2D? picture,
        ref ID3D11ShaderResourceView? pictureSrv,
        ref ID3D11ShaderResourceView[]? mipSrvs,
        ref ID3D11RenderTargetView[]? mipRtvs)
    {
        var paddedW = (uint)(width + 2 * Padding);
        var paddedH = (uint)(height + 2 * Padding);
        if (picture is not null
            && picture.Description.Width == paddedW
            && picture.Description.Height == paddedH)
        {
            return;
        }

        DisposeMips(ref mipSrvs, ref mipRtvs);
        pictureSrv?.Dispose();
        picture?.Dispose();

        var mipCount = (int)Math.Floor(Math.Log2(Math.Max(paddedW, paddedH))) + 1;
        Log.Info($"create picture {paddedW}x{paddedH} mips={mipCount} format={format} src={width}x{height}");
        picture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = paddedW,
            Height = paddedH,
            MipLevels = (uint)mipCount,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
        });
        pictureSrv = device.CreateShaderResourceView(picture);
        mipSrvs = new ID3D11ShaderResourceView[mipCount];
        mipRtvs = new ID3D11RenderTargetView[mipCount];
        for (var mip = 0; mip < mipCount; mip++)
        {
            mipSrvs[mip] = device.CreateShaderResourceView(picture, new ShaderResourceViewDescription
            {
                Format = format,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = { MostDetailedMip = (uint)mip, MipLevels = 1 },
            });
            mipRtvs[mip] = device.CreateRenderTargetView(picture, new RenderTargetViewDescription
            {
                Format = format,
                ViewDimension = RenderTargetViewDimension.Texture2D,
                Texture2D = { MipSlice = (uint)mip },
            });
            context.ClearRenderTargetView(mipRtvs[mip], new Color4(0, 0, 0, 1));
        }
    }

    private static void BuildGaussianPyramid(
        ID3D11DeviceContext context,
        ID3D11VertexShader vs,
        ID3D11PixelShader downsample,
        ID3D11Buffer cb,
        ID3D11SamplerState point,
        ID3D11RasterizerState raster,
        ID3D11Texture2D picture,
        ID3D11ShaderResourceView[] mipSrvs,
        ID3D11RenderTargetView[] mipRtvs)
    {
        var desc = picture.Description;
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.RSSetState(raster);
        context.VSSetShader(vs);
        context.PSSetShader(downsample);
        context.PSSetConstantBuffer(0, cb);
        context.PSSetSampler(0, point);

        for (var mip = 1; mip < mipRtvs.Length; mip++)
        {
            var srcW = Math.Max((int)desc.Width >> (mip - 1), 1);
            var srcH = Math.Max((int)desc.Height >> (mip - 1), 1);
            var dstW = Math.Max((int)desc.Width >> mip, 1);
            var dstH = Math.Max((int)desc.Height >> mip, 1);
            WriteConstants(context, cb, new FilterConstants
            {
                ScreenAndOrigin = new(1f / srcW, 1f / srcH, mip - 1, 0),
            });
            context.OMSetRenderTargets(mipRtvs[mip]);
            context.RSSetViewport(new Viewport(0, 0, dstW, dstH));
            context.PSSetShaderResource(0, mipSrvs[mip - 1]);
            context.Draw(3, 0);
        }

        context.ClearState();
    }

    private static void WriteConstants(ID3D11DeviceContext context, ID3D11Buffer cb, FilterConstants data)
    {
        var mapped = context.Map(cb, 0, MapMode.WriteDiscard);
        Marshal.StructureToPtr(data, mapped.DataPointer, false);
        context.Unmap(cb, 0);
    }

    private static void DisposeMips(ref ID3D11ShaderResourceView[]? srvs, ref ID3D11RenderTargetView[]? rtvs)
    {
        if (srvs is not null)
        {
            foreach (var view in srvs)
            {
                view.Dispose();
            }
        }

        if (rtvs is not null)
        {
            foreach (var view in rtvs)
            {
                view.Dispose();
            }
        }

        srvs = null;
        rtvs = null;
    }

    private void CreateDeviceForPrimary(
        nint hwnd,
        int overlayWidth,
        int overlayHeight,
        out ID3D11Device device,
        out ID3D11DeviceContext context,
        out IDXGIOutputDuplication duplication,
        out IDXGISwapChain1 swapChain,
        out int width,
        out int height)
    {
        var primary = System.Windows.Forms.Screen.PrimaryScreen
            ?? throw new InvalidOperationException("No primary screen.");
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        ID3D11Device? createdDevice = null;
        ID3D11DeviceContext? createdContext = null;
        IDXGIOutputDuplication? createdDup = null;

        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var name = output.Description.DeviceName?.Trim('\0') ?? "";
                        if (!name.Equals(primary.DeviceName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var levels = new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 };
                        var hr = Native.D3D11CreateDevice(
                            adapter.NativePointer,
                            (int)DriverType.Unknown,
                            DeviceCreationFlags.BgraSupport,
                            levels,
                            out var devicePtr,
                            out _,
                            out var contextPtr);
                        if (hr < 0 || devicePtr == 0 || contextPtr == 0)
                        {
                            throw new InvalidOperationException($"D3D11CreateDevice failed: 0x{hr:X8}");
                        }

                        createdDevice = new ID3D11Device(devicePtr);
                        createdContext = new ID3D11DeviceContext(contextPtr);

                        using var output1 = output.QueryInterface<IDXGIOutput1>();
                        try
                        {
                            createdDup = output1.DuplicateOutput(createdDevice);
                        }
                        catch (Exception ex)
                        {
                            Log.Error("DuplicateOutput failed", ex);
                            throw;
                        }
                        AdapterName = adapter.Description.Description?.Trim('\0') ?? "GPU";
                        width = overlayWidth;
                        height = overlayHeight;
                        Log.Info($"duplicate {name} adapter={AdapterName} overlay={width}x{height}");
                        device = createdDevice;
                        context = createdContext;
                        duplication = createdDup;

                        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
                        using var dxgiAdapter = dxgiDevice.GetAdapter();
                        using var factory2 = dxgiAdapter.GetParent<IDXGIFactory2>();
                        var swapDesc = new SwapChainDescription1
                        {
                            Width = (uint)width,
                            Height = (uint)height,
                            Format = Format.B8G8R8A8_UNorm,
                            SampleDescription = new SampleDescription(1, 0),
                            BufferUsage = Usage.RenderTargetOutput,
                            BufferCount = 2,
                            Scaling = Scaling.Stretch,
                            SwapEffect = SwapEffect.FlipDiscard,
                            AlphaMode = AlphaMode.Premultiplied,
                        };
                        try
                        {
                            swapChain = factory2.CreateSwapChainForHwnd(device, hwnd, swapDesc);
                        }
                        catch (Exception ex)
                        {
                            Log.Warn("premultiplied swapchain failed, falling back: " + Log.Describe(ex));
                            swapDesc.AlphaMode = AlphaMode.Ignore;
                            swapChain = factory2.CreateSwapChainForHwnd(device, hwnd, swapDesc);
                        }
                        factory2.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll);
                        return;
                    }
                }
            }
        }

        createdDup?.Dispose();
        createdContext?.Dispose();
        createdDevice?.Dispose();
        throw new InvalidOperationException("Could not find the GPU output that drives the primary screen.");
    }

    private static Blob Compile(string entry, string profile)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Filter.hlsl");
        var bytes = Encoding.UTF8.GetBytes(File.ReadAllText(path));
        var hr = Native.D3DCompile(
            bytes,
            entry,
            profile,
            (uint)ShaderFlags.OptimizationLevel3,
            out var blobPtr,
            out var errorPtr);
        Blob? error = errorPtr == 0 ? null : new Blob(errorPtr);
        if (blobPtr == 0 || hr < 0)
        {
            var details = error is not null
                ? Marshal.PtrToStringAnsi(error.BufferPointer) ?? $"0x{hr:X8}"
                : $"0x{hr:X8}";
            error?.Dispose();
            throw new InvalidOperationException($"HLSL {entry}: {details}");
        }

        error?.Dispose();
        return new Blob(blobPtr);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FilterConstants
    {
        public Vec4 Col0;
        public Vec4 Col1;
        public Vec4 Col2;
        public Vec4 ScreenAndOrigin;
        public Vec4 PaddedAndBlur;
        public Vec4 Shape;
        public Vec4 Light;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vec4
    {
        public float X, Y, Z, W;
        public Vec4(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public Vec4(double x, double y, double z, float w)
            : this((float)x, (float)y, (float)z, w)
        {
        }
    }
}
