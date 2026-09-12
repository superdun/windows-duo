using System.Runtime.InteropServices;

namespace WindowsDuo;

/// <summary>
/// Timestamps keyboard use without swallowing keys. Mouse / trackpad do not count,
/// so a lid close that brushes the pad still counts as idle.
/// </summary>
internal sealed class KeyboardIdle : IDisposable
{
    private readonly Native.LowLevelKeyboardProc _proc;
    private nint _hook;
    private long _lastKeyMs;

    public KeyboardIdle()
    {
        _proc = Hook;
        Touch();
    }

    public TimeSpan Idle => TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - Volatile.Read(ref _lastKeyMs)));

    public void Touch() => Volatile.Write(ref _lastKeyMs, Environment.TickCount64);

    public void Install()
    {
        if (_hook != 0)
        {
            return;
        }

        var module = Native.GetModuleHandleW("user32");
        _hook = Native.SetWindowsHookExW(Native.WhKeyboardLl, _proc, module, 0);
        if (_hook == 0)
        {
            Log.Warn($"keyboard idle hook failed: 0x{Marshal.GetLastPInvokeError():X8}");
        }
        else
        {
            Touch();
            Log.Info("keyboard idle hook installed");
        }
    }

    public void Uninstall()
    {
        if (_hook == 0)
        {
            return;
        }

        Native.UnhookWindowsHookEx(_hook);
        _hook = 0;
        Log.Info("keyboard idle hook removed");
    }

    public void Dispose() => Uninstall();

    private nint Hook(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && (wParam == Native.WmKeydown || wParam == Native.WmSyskeydown))
        {
            Touch();
        }

        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
