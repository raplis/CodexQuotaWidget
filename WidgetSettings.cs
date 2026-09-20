using System.Text.Json;
using System.IO;

namespace CodexQuotaWidget;

public sealed class WidgetSettings
{
    public int IntervalSeconds { get; set; } = 30;
    public double Opacity { get; set; } = .92;
    public double FontSize { get; set; } = 15;
    public string Theme { get; set; } = "mint";
    public bool Topmost { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool StartWithWindows { get; set; }
    public bool ShowStatus { get; set; } = true;
    public bool ShowUpdated { get; set; } = true;
    public bool ShowReset { get; set; } = true;
    public bool ShowCreditExpiry { get; set; } = true;
    public uint HotkeyModifiers { get; set; } = 0x0002 | 0x0001;
    public uint HotkeyKey { get; set; } = 0x43;
    public string HotkeyText { get; set; } = "Ctrl + Alt + C";
    public double? Left { get; set; }
    public double? Top { get; set; }
}

public sealed class SettingsStore
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CodexQuotaWidget", "settings.json");
    public WidgetSettings Load() { try { return File.Exists(_path) ? JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(_path)) ?? new() : new(); } catch { return new(); } }
    public void Save(WidgetSettings settings) { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); File.WriteAllText(_path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true })); }
}
