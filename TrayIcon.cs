using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WindowsDuo;

internal sealed class TrayIcon : IDisposable
{
    private readonly MainWindow _settings;
    private readonly NotifyIcon _notify;
    private readonly Icon _icon;
    private readonly ToolStripMenuItem _toggleItem;
    private bool _disposed;

    public TrayIcon(MainWindow settings)
    {
        _settings = settings;
        _icon = CreateIcon();
        _toggleItem = new ToolStripMenuItem("开始效果", null, (_, _) => Invoke(() => _settings.ToggleEffect()));
        var menu = new ContextMenuStrip();
        menu.Items.Add(_toggleItem);
        menu.Items.Add("设置", null, (_, _) => Invoke(() => _settings.ShowFromTray()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Invoke(() => _settings.ExitApp()));
        menu.Opening += (_, _) =>
        {
            _toggleItem.Text = _settings.IsEffectRunning ? "停止效果" : "开始效果";
        };

        _notify = new NotifyIcon
        {
            Icon = _icon,
            Text = "Windows Duo",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notify.MouseUp += OnMouseUp;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notify.Visible = false;
        _notify.Dispose();
        _icon.Dispose();
    }

    private void Invoke(Action action)
    {
        if (_settings.Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _settings.Dispatcher.Invoke(action);
        }
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        Invoke(() =>
        {
            if (_settings.IsVisible)
            {
                _settings.HideToTray();
            }
            else
            {
                _settings.ShowFromTray();
            }
        });
    }

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var fill = new SolidBrush(Color.FromArgb(201, 177, 138));
        using var dark = new SolidBrush(Color.FromArgb(40, 32, 24));
        graphics.FillRectangle(dark, 4, 6, 24, 20);
        graphics.FillPolygon(fill, new[]
        {
            new Point(4, 26),
            new Point(28, 14),
            new Point(28, 26),
        });
        var handle = bitmap.GetHicon();
        using var created = Icon.FromHandle(handle);
        var clone = (Icon)created.Clone();
        DestroyIcon(handle);
        return clone;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint hIcon);
}
