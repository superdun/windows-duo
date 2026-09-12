using System.Runtime.InteropServices;

namespace WindowsDuo;

/// <summary>
/// Listens for Escape while the overlay is up, even if no window has focus.
/// </summary>
internal sealed class EscHook : IDisposable
{
    private readonly Native.LowLevelKeyboardProc _proc;
    private nint _hook;

    public event Action? EscPressed;

    public EscHook()
    {
        _proc = Hook;
    }

    public bool Installed => _hook != 0;

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
            Log.Warn($"Esc hook failed: 0x{Marshal.GetLastPInvokeError():X8}");
        }
        else
        {
            Log.Info("Esc hook installed");
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
        Log.Info("Esc hook removed");
    }

    public void Dispose() => Uninstall();

    private nint Hook(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && (wParam == Native.WmKeydown || wParam == Native.WmSyskeydown))
        {
            var vk = Marshal.ReadInt32(lParam);
            if (vk == Native.VkEscape)
            {
                EscPressed?.Invoke();
                return 1;
            }
        }

        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
