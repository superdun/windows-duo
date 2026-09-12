using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WindowsDuo;

public partial class MainWindow : Window
{
    private readonly GpuDesktopFilter _filter = new();
    private readonly DispatcherTimer _statusTimer;
    private readonly LidAutoFold _lid;
    private const int HotkeyId = 1;
    private readonly EscHook _esc = new();
    private bool _allowClose;
    private DateTime _shownAt;
    private bool _syncingSlider;

    public event Action? TrayDisposed;

    public bool IsEffectRunning => _filter.Running;

    public MainWindow()
    {
        InitializeComponent();
        _lid = new LidAutoFold(_filter, Dispatcher);
        _lid.Changed += RefreshStatus;
        _esc.EscPressed += () => Dispatcher.BeginInvoke(StopEffect);
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        Loaded += async (_, _) =>
        {
            Log.Info($"ui loaded log={Log.FilePath}");
            _filter.UpdateParams(ReadParams());
            _lid.Sensitivity = (float)SensitivitySlider.Value;
            _statusTimer.Start();
            await _lid.StartAsync();
            RefreshStatus();
        };
        Closed += (_, _) =>
        {
            UnregisterHotkey();
            _esc.Dispose();
            _statusTimer.Stop();
            _lid.Dispose();
            _filter.Dispose();
        };
    }

    public void ShowFromTray()
    {
        _shownAt = DateTime.UtcNow;
        UpdateLabels();
        RefreshStatus();
        var work = SystemParameters.WorkArea;
        Left = work.Right - Width - 16;
        Top = Math.Max(work.Top + 16, work.Bottom - Height - 16);
        Show();
        Activate();
    }

    public void HideToTray()
    {
        Hide();
    }

    public void ToggleEffect() => Toggle();

    private void StopEffect()
    {
        if (_filter.Running)
        {
            Log.Info("esc stop");
            _lid.ManualPreview = false;
            _lid.CancelToIdle();
            _filter.Stop();
            _esc.Uninstall();
            ToggleButton.Content = "开始实时效果";
            RefreshStatus();
        }
    }

    public void ExitApp()
    {
        _allowClose = true;
        _lid.Dispose();
        _filter.Dispose();
        TrayDisposed?.Invoke();
        Close();
        Application.Current.Shutdown();
    }

    private EffectParams ReadParams() => new()
    {
        Angle = (float)AngleSlider.Value,
        Amount = (float)AmountSlider.Value,
        Gradient = (float)GradientSlider.Value,
        Highlight = (float)HighlightSlider.Value,
        Sheen = (float)SheenSlider.Value,
    };

    private void UpdateLabels()
    {
        AngleValue.Text = $"{AngleSlider.Value:0}°";
        AmountValue.Text = $"{AmountSlider.Value:0}°";
        GradientValue.Text = $"{GradientSlider.Value:0.00}";
        HighlightValue.Text = $"{HighlightSlider.Value:0.00}";
        SensitivityValue.Text = $"{SensitivitySlider.Value:0.00}×";
        SheenValue.Text = $"{SheenSlider.Value:0.00}";
    }

    private void OnParamChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _syncingSlider)
        {
            return;
        }

        UpdateLabels();
        _lid.Sensitivity = (float)SensitivitySlider.Value;
        _filter.UpdateParams(ReadParams());
    }

    private void OnHorizontal(object sender, RoutedEventArgs e) => AngleSlider.Value = 0;

    private void OnVertical(object sender, RoutedEventArgs e) => AngleSlider.Value = 90;

    private void OnToggle(object sender, RoutedEventArgs e) => Toggle();

    private void Toggle()
    {
        if (_filter.Running)
        {
            Log.Info("ui stop");
            _lid.ManualPreview = false;
            _filter.ClearAutoFold();
            _filter.Stop();
            _esc.Uninstall();
            ToggleButton.Content = "开始实时效果";
        }
        else
        {
            Log.Info("ui start");
            _lid.ManualPreview = true;
            _filter.ClearAutoFold();
            _filter.UpdateParams(ReadParams());
            _filter.SnapShown((float)AmountSlider.Value);
            _filter.Start();
            _esc.Install();
            ToggleButton.Content = "停止实时效果";
        }

        RefreshStatus();
    }

    private void OnAutoFoldChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _lid.SetEnabled(AutoFoldBox.IsChecked == true);
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (_lid.Driving && !_syncingSlider)
        {
            _syncingSlider = true;
            AmountSlider.Value = _filter.ShownAmount;
            _syncingSlider = false;
        }

        UpdateLabels();
        if (!string.IsNullOrEmpty(_filter.LastError))
        {
            StatusText.Text = _filter.LastError;
            ToggleButton.Content = "开始实时效果";
            _lid.ManualPreview = false;
            _filter.Stop();
            _esc.Uninstall();
            return;
        }

        if (_filter.Running)
        {
            var fold = _lid.Driving ? _lid.Status : "透视虚化";
            StatusText.Text = $"{_filter.AdapterName}  ·  {_filter.Fps:0} fps  ·  {fold}";
            ToggleButton.Content = "停止实时效果";
            if (_lid.Driving && !_esc.Installed)
            {
                _esc.Install();
            }
        }
        else
        {
            StatusText.Text = $"{_lid.Status}。左键托盘图标打开设置。";
            ToggleButton.Content = "开始实时效果";
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.ExcludeFromCapture(hwnd);
        Native.RegisterHotKey(hwnd, HotkeyId, Native.ModControl | Native.ModShift | Native.ModNorepeat, 0x42);
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);
    }

    private void UnregisterHotkey()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0)
        {
            Native.UnregisterHotKey(hwnd, HotkeyId);
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == Native.WmHotkey && wParam == HotkeyId)
        {
            handled = true;
            Dispatcher.Invoke(Toggle);
        }

        return 0;
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_filter.Running)
            {
                StopEffect();
            }

            HideToTray();
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (DateTime.UtcNow - _shownAt < TimeSpan.FromMilliseconds(400))
        {
            return;
        }

        HideToTray();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        HideToTray();
    }
}
