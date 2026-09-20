using Microsoft.Win32;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using Media = System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using DrawingIcon = System.Drawing.Icon;

namespace CodexQuotaWidget;

public partial class MainWindow : Window
{
    private const int HotKeyId = 5101;
    private readonly SettingsStore _settingsStore = new();
    private readonly CodexUsageClient _usageClient = new();
    private readonly DispatcherTimer _timer = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly string _cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CodexQuotaWidget", "last-quota.json");
    private WidgetSettings _settings = new();
    private Forms.NotifyIcon? _tray;
    private Icon? _trayIcon;
    private HwndSource? _source;
    private bool _exiting;
    private bool _loadingSettings;
    private bool _lunaAvailable;
    private const int ShowWidgetMessage = 0x8001;

    public MainWindow()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = _settingsStore.Load(); _loadingSettings = true; ApplySettings(); _loadingSettings = false;
            _timer.Interval = TimeSpan.FromSeconds(_settings.IntervalSeconds > 0 ? _settings.IntervalSeconds : 30);
            if (_source != null) { UnregisterHotKey(_source.Handle, HotKeyId); RegisterHotkey(); }
            RestoreWindowPosition(); SetupTray(); UpdateStartupRegistration();
            var launchedByStartup = Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
            if (_settings.StartMinimized && launchedByStartup) { Hide(); _tray?.ShowBalloonTip(2500, "Codex 额度", "已在托盘后台运行，点击托盘图标显示窗口", Forms.ToolTipIcon.Info); }
            else _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(ShowWindowFromTray));
            LogStartup($"started; minimized={_settings.StartMinimized}; interval={_settings.IntervalSeconds}");
            if (_settings.IntervalSeconds > 0) _timer.Start();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            LogStartup($"startup error: {ex.GetType().Name}: {ex.Message}");
            Show(); Activate(); StatusText.Text = "启动异常 · 已显示窗口";
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = (HwndSource)PresentationSource.FromVisual(this)!; _source.AddHook(WndProc); RegisterHotkey();
    }

    private async Task RefreshAsync()
    {
        if (!await _refreshGate.WaitAsync(0)) return;
        try
        {
            RefreshButton.IsEnabled = false; StatusText.Text = "正在刷新额度…";
            var quota = await _usageClient.FetchAsync(); SaveCache(quota); Render(quota);
        }
        catch (Exception ex)
        {
            var cached = LoadCache();
            if (cached != null) { cached.IsLive = false; Render(cached, $"上次成功数据 · {CacheAge(cached)}"); }
            else Render(new Quota(), ex.Message.Contains("登录") ? "请先登录 Codex CLI" : "额度刷新失败 · 请检查网络");
        }
        finally { RefreshButton.IsEnabled = true; _refreshGate.Release(); }
    }

    private void Render(Quota q, string? status = null)
    {
        var fiveHour = Math.Clamp(100 - q.FiveHourUsed, 0, 100);
        var week = Math.Clamp(100 - q.WeekUsed, 0, 100);
        var luna = Math.Clamp(100 - q.LunaUsed, 0, 100);
        FiveHourSection.Visibility = q.IsLive || q.FiveHourReset.HasValue ? Visibility.Visible : Visibility.Collapsed;
        _lunaAvailable = q.HasLunaReserve;
        LunaSection.Visibility = _lunaAvailable ? Visibility.Visible : Visibility.Collapsed;
        FiveHourBar.Value = q.FiveHourUsed; FiveHourText.Text = $"剩余 {fiveHour:0}%";
        FiveHourText.Foreground = RemainingBrush(fiveHour); FiveHourBar.Foreground = RemainingBrush(fiveHour);
        WeekBar.Value = q.WeekUsed; WeekText.Text = $"剩余 {week:0}%";
        WeekText.Foreground = RemainingBrush(week); WeekBar.Foreground = RemainingBrush(week);
        LunaBar.Value = q.LunaUsed; LunaText.Text = $"剩余 {luna:0}%";
        LunaText.Foreground = RemainingBrush(luna); LunaBar.Foreground = RemainingBrush(luna);
        StatusText.Text = status ?? (q.IsLive ? (_settings.IntervalSeconds > 0 ? $"实时额度 · 每 {_settings.IntervalSeconds} 秒更新" : "实时额度 · 自动更新已关闭") : "暂无实时额度"); StatusDot.Fill = q.IsLive ? RemainingBrush(week) : (Media.Brush)FindResource("Amber");
        UpdatedText.Text = $"更新于 {q.UpdatedAt:MM-dd HH:mm:ss}"; ResetText.Text = $"5h重置 {FormatReset(q.FiveHourReset)} · 周重置 {FormatReset(q.WeekReset)}\nLuna重置 {FormatReset(q.LunaReset)}"; UpdateTrayIcon(week, q.IsLive);
    }
    private string CacheAge(Quota q) { var age = DateTime.Now - q.UpdatedAt; return age.TotalSeconds < 60 ? $"{Math.Max(1, age.Seconds)} 秒前" : $"{Math.Max(1, (int)age.TotalMinutes)} 分钟前"; }
    private static string FormatReset(DateTime? value) => value == null ? "未知" : value.Value.ToLocalTime().ToString("MM-dd HH:mm");
    private static Media.Brush RemainingBrush(double remaining) => new Media.SolidColorBrush(Interpolate(remaining >= 50 ? "#B7E46C" : "#F0B765", remaining >= 50 ? "#F0B765" : "#F06A6A", remaining >= 50 ? (100 - remaining) / 50 : (50 - remaining) / 50));
    private static System.Windows.Media.Color Interpolate(string from, string to, double amount) { amount = Math.Clamp(amount, 0, 1); var a = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(from); var b = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(to); return System.Windows.Media.Color.FromRgb((byte)(a.R + (b.R - a.R) * amount), (byte)(a.G + (b.G - a.G) * amount), (byte)(a.B + (b.B - a.B) * amount)); }

    private void ApplySettings()
    {
        Opacity = _settings.Opacity; Topmost = _settings.Topmost; OpacitySlider.Value = _settings.Opacity; FontSizeSlider.Value = _settings.FontSize; FontSize_Changed(this, new RoutedPropertyChangedEventArgs<double>(0, _settings.FontSize));
        TopmostCheck.IsChecked = _settings.Topmost; StartMinimizedCheck.IsChecked = _settings.StartMinimized; StartWithWindowsCheck.IsChecked = _settings.StartWithWindows; HotkeyCapture.Content = _settings.HotkeyText;
        IntervalCombo.SelectedIndex = _settings.IntervalSeconds switch { 60 => 1, 0 => 2, _ => 0 }; ThemeCombo.SelectedIndex = _settings.Theme switch { "blue" => 1, "violet" => 2, "coral" => 3, _ => 0 }; ApplyTheme(_settings.Theme);
    }
    private void SaveSettings() { if (_loadingSettings || !IsInitialized || FontSizeSlider == null || TopmostCheck == null || StartMinimizedCheck == null || StartWithWindowsCheck == null) return; _settings.Opacity = Opacity; _settings.FontSize = FontSizeSlider.Value; _settings.Topmost = TopmostCheck.IsChecked == true; _settings.StartMinimized = StartMinimizedCheck.IsChecked == true; _settings.StartWithWindows = StartWithWindowsCheck.IsChecked == true; _settings.Left = Left; _settings.Top = Top; _settingsStore.Save(_settings); }
    private void RestoreWindowPosition()
    {
        var dpi = Media.VisualTreeHelper.GetDpi(this); var sx = dpi.DpiScaleX; var sy = dpi.DpiScaleY;
        var saved = _settings.Left.HasValue && _settings.Top.HasValue ? new System.Drawing.Point((int)(_settings.Left.Value * sx), (int)(_settings.Top.Value * sy)) : System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea.Location;
        var area = System.Windows.Forms.Screen.FromPoint(saved).WorkingArea;
        var left = area.Left / sx; var right = area.Right / sx; var top = area.Top / sy; var bottom = area.Bottom / sy;
        Left = _settings.Left ?? right - Width - 18; Top = _settings.Top ?? bottom - Height - 18;
        if (Left + Width < left + 30 || Left > right - 30) Left = right - Width - 18; if (Top + Height < top + 30 || Top > bottom - 30) Top = bottom - Height - 18;
    }

    private void SaveCache(Quota q) { Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!); File.WriteAllText(_cachePath, System.Text.Json.JsonSerializer.Serialize(q)); }
    private Quota? LoadCache() { try { return File.Exists(_cachePath) ? System.Text.Json.JsonSerializer.Deserialize<Quota>(File.ReadAllText(_cachePath)) : null; } catch { return null; } }
    private void LogStartup(string message) { try { var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CodexQuotaWidget", "startup.log"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.AppendAllText(path, $"{DateTime.Now:O} {message}{Environment.NewLine}"); } catch { } }
    private void SetupTray()
    {
        _tray = new Forms.NotifyIcon { Icon = DrawingIcon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Information, Visible = true, Text = "Codex 额度" }; var menu = new Forms.ContextMenuStrip(); menu.Items.Add("显示 / 隐藏", null, (_, _) => ToggleVisible()); menu.Items.Add("刷新额度", null, async (_, _) => await RefreshAsync()); menu.Items.Add("退出", null, (_, _) => ForceExit()); _tray.ContextMenuStrip = menu; _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowWindowFromTray(); };
    }
    private void UpdateTrayIcon(double remaining, bool live)
    {
        if (_tray == null) return; _tray.Text = live ? $"Codex 额度 · 剩余 {remaining:0}%" : "Codex 额度 · 数据过期";
        using var bitmap = new Bitmap(16, 16); using var graphics = Graphics.FromImage(bitmap); graphics.Clear(Color.Transparent); var color = live ? System.Drawing.Color.FromArgb(255, (byte)Math.Clamp(183 + (100 - remaining) * 1.1, 0, 255), (byte)Math.Clamp(228 - (100 - remaining) * 2.2, 0, 255), (byte)Math.Clamp(108 - (100 - remaining) * .5, 0, 255)) : Color.Gray; using var brush = new SolidBrush(color); graphics.FillEllipse(brush, 1, 1, 14, 14); var handle = bitmap.GetHicon(); var next = DrawingIcon.FromHandle(handle); _trayIcon?.Dispose(); _trayIcon = next; _tray.Icon = _trayIcon;
    }
    private void ToggleVisible() { if (IsVisible) Hide(); else { Show(); Activate(); } }
    private void ShowWindowFromTray()
    {
        if (!IsVisible) Show();
        WindowState = WindowState.Normal;
        EnsureWindowOnScreen();
        var desiredTopmost = _settings.Topmost;
        Topmost = true;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) { ShowWindow(handle, 9); Activate(); SetForegroundWindow(handle); }
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => { Topmost = desiredTopmost; Activate(); if (handle != IntPtr.Zero) { ShowWindow(handle, 9); SetForegroundWindow(handle); } }));
    }
    private void EnsureWindowOnScreen()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && GetWindowRect(handle, out var actual))
        {
            var center = new System.Drawing.Point((actual.Left + actual.Right) / 2, (actual.Top + actual.Bottom) / 2);
            var work = Forms.Screen.FromPoint(center).WorkingArea;
            var fullyInside = actual.Left >= work.Left && actual.Top >= work.Top && actual.Right <= work.Right && actual.Bottom <= work.Bottom;
            if (!fullyInside)
            {
                var width = actual.Right - actual.Left; var height = actual.Bottom - actual.Top;
                SetWindowPos(handle, IntPtr.Zero, work.Right - width - 18, work.Bottom - height - 18, 0, 0, 0x0001 | 0x0004 | 0x0040);
            }
            return;
        }
        var dpi = Media.VisualTreeHelper.GetDpi(this); var sx = dpi.DpiScaleX; var sy = dpi.DpiScaleY;
        var point = new System.Drawing.Point((int)(Left * sx), (int)(Top * sy));
        var area = Forms.Screen.FromPoint(point).WorkingArea;
        var left = area.Left / sx; var right = area.Right / sx; var top = area.Top / sy; var bottom = area.Bottom / sy;
        if (Left + Width < left + 30 || Left > right - 30) Left = right - Width - 18;
        if (Top + Height < top + 30 || Top > bottom - 30) Top = bottom - Height - 18;
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();
    private void Collapse_Click(object sender, RoutedEventArgs e)
    {
        var collapsed = WeekSection.Visibility == Visibility.Visible;
        WeekSection.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        LunaSection.Visibility = collapsed || !_lunaAvailable ? Visibility.Collapsed : Visibility.Visible;
        QuotaTitle.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        FiveHourBar.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        FooterBorder.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        QuotaContent.Margin = collapsed ? new Thickness(0, 8, 0, 0) : new Thickness(0, 18, 0, 12);
        SettingsPanel.Visibility = Visibility.Collapsed;
        Height = collapsed ? 125 : 370;
        CollapseGlyph.Text = collapsed ? "⌄" : "⌃";
        CollapseButton.ToolTip = collapsed ? "展开" : "折叠";
        EnsureWindowOnScreen();
        SaveSettings();
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (IsLoaded) { Opacity = e.NewValue; SaveSettings(); } }
    private void FontSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (FiveHourText != null) { FiveHourText.FontSize = e.NewValue; WeekText.FontSize = e.NewValue; LunaText.FontSize = e.NewValue; if (IsLoaded) SaveSettings(); } }
    private void TopmostCheck_Click(object sender, RoutedEventArgs e) { Topmost = TopmostCheck.IsChecked == true; SaveSettings(); }
    private void Topmost_Click(object sender, RoutedEventArgs e) { Topmost = !Topmost; TopmostCheck.IsChecked = Topmost; SaveSettings(); }
    private void Interval_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { if (!IsInitialized || _loadingSettings || IntervalCombo.SelectedItem is not System.Windows.Controls.ComboBoxItem item || !int.TryParse(item.Tag?.ToString(), out var seconds)) return; _settings.IntervalSeconds = seconds; if (seconds == 0) _timer.Stop(); else { _timer.Interval = TimeSpan.FromSeconds(seconds); _timer.Start(); } SaveSettings(); if (IsLoaded) StatusText.Text = seconds == 0 ? "自动更新已关闭" : $"实时额度 · 每 {seconds} 秒更新"; }
    private void Theme_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { if (!IsLoaded || ThemeCombo.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return; _settings.Theme = item.Tag?.ToString() ?? "mint"; ApplyTheme(_settings.Theme); SaveSettings(); }
    private void ApplyTheme(string key)
    {
        var green = key switch { "blue" => "#8FD7FF", "violet" => "#C6A7FF", "coral" => "#FF9B7A", _ => "#B7E46C" };
        var amber = key switch { "blue" => "#72B9E8", "violet" => "#A987EA", "coral" => "#F28B68", _ => "#F0B765" };
        System.Windows.Application.Current.Resources["Green"] = new Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(green));
        System.Windows.Application.Current.Resources["Amber"] = new Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(amber));
    }
    private void StartMinimized_Click(object sender, RoutedEventArgs e) => SaveSettings();
    private void StartWithWindows_Click(object sender, RoutedEventArgs e) { SaveSettings(); UpdateStartupRegistration(); }
    private void HotkeyCapture_Click(object sender, RoutedEventArgs e) { _capturingHotkey = true; HotkeyCapture.Content = "请按下快捷键…"; HotkeyCapture.Focus(); }
    private bool _capturingHotkey;
    private void HotkeyCapture_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (!_capturingHotkey || _source == null) return; var key = e.Key == Key.System ? e.SystemKey : e.Key; if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return; var modifiers = Keyboard.Modifiers; uint native = 0; if (modifiers.HasFlag(ModifierKeys.Control)) native |= 2; if (modifiers.HasFlag(ModifierKeys.Alt)) native |= 1; if (modifiers.HasFlag(ModifierKeys.Shift)) native |= 4; if (modifiers.HasFlag(ModifierKeys.Windows)) native |= 8; var vk = (uint)KeyInterop.VirtualKeyFromKey(key); if (vk == 0) return; UnregisterHotKey(_source.Handle, HotKeyId); _settings.HotkeyModifiers = native; _settings.HotkeyKey = vk; _settings.HotkeyText = FormatHotkey(modifiers, key); if (!RegisterHotKey(_source.Handle, HotKeyId, native, vk)) { HotkeyCapture.Content = "注册失败 · 请换一个快捷键"; } else { HotkeyCapture.Content = _settings.HotkeyText; SaveSettings(); } _capturingHotkey = false; e.Handled = true; }
    private static string FormatHotkey(ModifierKeys modifiers, Key key) { var p = new List<string>(); if (modifiers.HasFlag(ModifierKeys.Control)) p.Add("Ctrl"); if (modifiers.HasFlag(ModifierKeys.Alt)) p.Add("Alt"); if (modifiers.HasFlag(ModifierKeys.Shift)) p.Add("Shift"); if (modifiers.HasFlag(ModifierKeys.Windows)) p.Add("Win"); p.Add(key.ToString()); return string.Join(" + ", p); }
    private void RegisterHotkey() { if (_source != null && !RegisterHotKey(_source.Handle, HotKeyId, _settings.HotkeyModifiers, _settings.HotkeyKey)) HotkeyCapture.Content = "注册失败 · 请换一个快捷键"; }
    private void WindowDrag(object sender, MouseButtonEventArgs e) { if (IsInteractive(e.OriginalSource as DependencyObject)) return; if (e.ButtonState == MouseButtonState.Pressed) { DragMove(); SaveSettings(); } }
    private static bool IsInteractive(DependencyObject? source) { while (source != null) { if (source is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.Thumb or System.Windows.Controls.Primitives.ScrollBar or System.Windows.Controls.Slider or System.Windows.Controls.ComboBox or System.Windows.Controls.CheckBox or System.Windows.Controls.TextBox) return true; source = Media.VisualTreeHelper.GetParent(source); } return false; }
    private void Exit_Click(object sender, RoutedEventArgs e) { if (StartMinimizedCheck.IsChecked == true) Hide(); else ForceExit(); }
    private void ContextExit_Click(object sender, RoutedEventArgs e) => ForceExit();
    private void ForceExit() { _exiting = true; System.Windows.Application.Current.Shutdown(); }
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e) { SaveSettings(); if (!_exiting && StartMinimizedCheck.IsChecked == true) { e.Cancel = true; Hide(); } else { _timer.Stop(); _tray?.Dispose(); _trayIcon?.Dispose(); if (_source != null) UnregisterHotKey(_source.Handle, HotKeyId); } }
    private void UpdateStartupRegistration() { const string key = "Software\\Microsoft\\Windows\\CurrentVersion\\Run"; using var run = Registry.CurrentUser.OpenSubKey(key, true); if (run == null) return; if (_settings.StartWithWindows) run.SetValue("CodexQuotaWidget", $"\"{Environment.ProcessPath}\" --minimized"); else run.DeleteValue("CodexQuotaWidget", false); }
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) { if (msg == 0x0312 && wParam.ToInt32() == HotKeyId) { ToggleVisible(); handled = true; } else if (msg == ShowWidgetMessage) { ShowWindowFromTray(); handled = true; } return IntPtr.Zero; }
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out WindowRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { public int Left, Top, Right, Bottom; }
}
