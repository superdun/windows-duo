using System.Runtime.InteropServices;

namespace WindowsDuo;

internal static class Native
{
    public const int GwlExStyle = -20;
    public const int WsExTransparent = 0x00000020;
    public const int WsExToolWindow = 0x00000080;
    public const int WsExNoActivate = 0x08000000;
    public const int WsExTopmost = 0x00000008;
    public const int WsExNoRedirectionBitmap = 0x00200000;
    public const int WsPopup = unchecked((int)0x80000000);
    public const int SwHide = 0;
    public const int SwShow = 5;
    public const int SwShowNoActivate = 4;
    public const uint WdaExcludeFromCapture = 0x00000011;
    public const int WmHotkey = 0x0312;
    public const uint WmNcHitTest = 0x0084;
    public static readonly nint HtTransparent = -1;
    public const int WmDestroy = 0x0002;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModNorepeat = 0x4000;
    public static readonly nint HwndTopmost = -1;
    public const uint SwpNomove = 0x0002;
    public const uint SwpNosize = 0x0001;
    public const uint SwpNoactivate = 0x0010;
    public const uint SwpShowwindow = 0x0040;
    public const int CsHRedraw = 0x0002;
    public const int CsVRedraw = 0x0001;
    public const int ColorWindow = 5;

    public delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public nint WndProc;
        public int ClsExtra;
        public int WndExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
        public nint IconSm;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    public static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassExW(ref WndClassEx lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint CreateWindowExW(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        nint hWndParent,
        nint hMenu,
        nint hInstance,
        nint lpParam);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint FindWindowW(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(nint hwnd, nint hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(nint hwnd, out Rect lpRect);

    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(nint hwnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    public const uint PwRenderFullContent = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);

    public static void MakeDwmSheet(nint hwnd)
    {
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandleW(string? lpModuleName);

    public static nint GetWindowLongPtr(nint hWnd, int nIndex) =>
        nint.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);

    public static nint SetWindowLongPtr(nint hWnd, int nIndex, nint value) =>
        nint.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, value) : SetWindowLong32(hWnd, nIndex, (int)value);

    public static void ExcludeFromCapture(nint hwnd) =>
        SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);

    [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
    public static extern int D3D11CreateDevice(
        nint adapter,
        int driverType,
        nint software,
        uint flags,
        [In] Vortice.Direct3D.FeatureLevel[] featureLevels,
        int featureLevelCount,
        int sdkVersion,
        out nint device,
        out Vortice.Direct3D.FeatureLevel featureLevel,
        out nint context);

    public static int D3D11CreateDevice(
        nint adapter,
        int driverType,
        Vortice.Direct3D11.DeviceCreationFlags flags,
        Vortice.Direct3D.FeatureLevel[] featureLevels,
        out nint device,
        out Vortice.Direct3D.FeatureLevel featureLevel,
        out nint context) =>
        D3D11CreateDevice(
            adapter,
            driverType,
            0,
            (uint)flags,
            featureLevels,
            featureLevels.Length,
            7,
            out device,
            out featureLevel,
            out context);

    [DllImport("d3dcompiler_47.dll", EntryPoint = "D3DCompile", CharSet = CharSet.Ansi)]
    public static extern int D3DCompile(
        [In] byte[] srcData,
        nuint srcDataSize,
        string sourceName,
        nint defines,
        nint include,
        string entryPoint,
        string target,
        uint flags1,
        uint flags2,
        out nint code,
        out nint errors);

    public static int D3DCompile(
        byte[] srcData,
        string entryPoint,
        string target,
        uint flags1,
        out nint code,
        out nint errors) =>
        D3DCompile(
            srcData,
            (nuint)srcData.Length,
            "Filter.hlsl",
            0,
            0,
            entryPoint,
            target,
            flags1,
            0,
            out code,
            out errors);

    public static void RaiseTopmost(nint hwnd) =>
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNomove | SwpNosize | SwpNoactivate | SwpShowwindow);

    public const int WhKeyboardLl = 13;
    public const int WmKeydown = 0x0100;
    public const int WmSyskeydown = 0x0104;
    public const int VkEscape = 0x1B;

    public delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW")]
    public static extern nint SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    public static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);
}
