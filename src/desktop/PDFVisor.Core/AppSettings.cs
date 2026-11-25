using System.Text.Json;

namespace PDFVisor.Core;

public sealed class AppSettings
{
    public double WindowWidth { get; set; } = 1400;
    public double WindowHeight { get; set; } = 880;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool IsMaximized { get; set; }
    public bool IsSidebarOpen { get; set; } = true;
    public bool DarkReadingMode { get; set; }
    public double DefaultZoom { get; set; } = 1.0;
    public string Language { get; set; } = "es";
    public bool RestoreOpenTabs { get; set; } = true;
    public List<string> LastOpenTabPaths { get; set; } = [];
    public int LastActiveTabIndex { get; set; }

    public static string DefaultStoragePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PDFVisor",
            "settings.json");

    public static AppSettings Load(string? path = null)
    {
        string target = path ?? DefaultStoragePath;
        try
        {
            if (!File.Exists(target)) return new AppSettings();
            string json = File.ReadAllText(target);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(string? path = null)
    {
        string target = path ?? DefaultStoragePath;
        try
        {
            string? dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(target, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Ignore settings persistence failures on restricted profiles
        }
    }
}
