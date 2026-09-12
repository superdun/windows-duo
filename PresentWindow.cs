using System.Drawing;
using System.Runtime.InteropServices;

namespace WindowsDuo;

internal sealed class PresentWindow : IDisposable
{
    private const string ClassName = "WindowsDuoGpuPresent";
    private static readonly Native.WndProc WndProcKeepAlive = WndProc;
    private static bool _classRegistered;
    private nint _hwnd;

    public nint Handle => _hwnd;
    public Rectangle OverlayBounds { get; private set; }
    public Rectangle DesktopBounds { get; private set; }
    public Rectangle WorkingArea { get; private set; }

    public void ShowOnPrimary()
    {
        EnsureClass();
        var screen = System.Windows.Forms.Screen.PrimaryScreen
            ?? throw new InvalidOperationException("No primary screen.");
        DesktopBounds = screen.Bounds;
        OverlayBounds = screen.Bounds;
        WorkingArea = screen.WorkingArea;
        var b = OverlayBounds;
        var instance = Native.GetModuleHandleW(null);
        var ex = Native.WsExTopmost
            | Native.WsExTransparent
            | Native.WsExToolWindow
            | Native.WsExNoActivate
            | Native.WsExNoRedirectionBitmap;
        _hwnd = Native.CreateWindowExW(
            ex,
            ClassName,
            "WindowsDuo Present",
            Native.WsPopup,
            b.X,
            b.Y,
            b.Width,
            b.Height,
            0,
            0,
            instance,
            0);
        if (_hwnd == 0)
        {
            ex = Native.WsExTopmost | Native.WsExTransparent | Native.WsExToolWindow | Native.WsExNoActivate;
            _hwnd = Native.CreateWindowExW(
                ex,
                ClassName,
                "WindowsDuo Present",
                Native.WsPopup,
                b.X,
                b.Y,
                b.Width,
                b.Height,
                0,
                0,
                instance,
                0);
        }

        if (_hwnd == 0)
        {
            throw new InvalidOperationException("Failed to create present window.");
        }

        Native.ExcludeFromCapture(_hwnd);
        Native.MakeDwmSheet(_hwnd);
        Log.Info($"present window hwnd=0x{_hwnd:X} overlay={b.X},{b.Y} {b.Width}x{b.Height} desktop={DesktopBounds.Width}x{DesktopBounds.Height}");
    }

    public void Reveal()
    {
        if (_hwnd == 0)
        {
            return;
        }

        Native.ShowWindow(_hwnd, Native.SwShowNoActivate);
        Log.Info("present window revealed");
    }

    public void Conceal()
    {
        if (_hwnd == 0)
        {
            return;
        }

        Native.ShowWindow(_hwnd, Native.SwHide);
        Log.Info("present window concealed");
    }

    public void Dispose()
    {
        if (_hwnd != 0)
        {
            Native.DestroyWindow(_hwnd);
            _hwnd = 0;
        }
    }

    private static void EnsureClass()
    {
        if (_classRegistered)
        {
            return;
        }

        var wnd = new Native.WndClassEx
        {
            Size = (uint)Marshal.SizeOf<Native.WndClassEx>(),
            Style = Native.CsHRedraw | Native.CsVRedraw,
            WndProc = Marshal.GetFunctionPointerForDelegate(WndProcKeepAlive),
            Instance = Native.GetModuleHandleW(null),
            ClassName = ClassName,
            Background = 0,
        };
        if (Native.RegisterClassExW(ref wnd) == 0)
        {
            throw new InvalidOperationException("Failed to register present window class.");
        }

        _classRegistered = true;
    }

    private static nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == Native.WmNcHitTest)
        {
            return Native.HtTransparent;
        }

        return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
    }
}
