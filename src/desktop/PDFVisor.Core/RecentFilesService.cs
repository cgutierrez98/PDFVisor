using System.Text.Json;

namespace PDFVisor.Core;

public sealed record RecentFileItem(string FilePath, string FileName, DateTime LastOpenedUtc)
{
    public string DirectoryDisplay => Path.GetDirectoryName(FilePath) ?? string.Empty;
}

public static class RecentFilesService
{
    private const int MaxEntries = 10;

    private static string StoragePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PDFVisor",
            "recent.json");

    public static IReadOnlyList<RecentFileItem> Load()
    {
        try
        {
            if (!File.Exists(StoragePath)) return Array.Empty<RecentFileItem>();
            string json = File.ReadAllText(StoragePath);
            var items = JsonSerializer.Deserialize<List<RecentFileItem>>(json) ?? [];
            return items
                .Where(i => !string.IsNullOrWhiteSpace(i.FilePath) && File.Exists(i.FilePath))
                .OrderByDescending(i => i.LastOpenedUtc)
                .Take(MaxEntries)
                .ToList();
        }
        catch
        {
            return Array.Empty<RecentFileItem>();
        }
    }

    public static IReadOnlyList<RecentFileItem> Record(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return Load();
        string full = Path.GetFullPath(filePath);
        var list = Load()
            .Where(i => !string.Equals(i.FilePath, full, StringComparison.OrdinalIgnoreCase))
            .ToList();

        list.Insert(0, new RecentFileItem(full, Path.GetFileName(full), DateTime.UtcNow));
        if (list.Count > MaxEntries) list.RemoveRange(MaxEntries, list.Count - MaxEntries);

        try
        {
            string? dir = Path.GetDirectoryName(StoragePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(StoragePath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Ignore persistence errors on restricted profiles
        }

        return list;
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(StoragePath)) File.Delete(StoragePath);
        }
        catch { }
    }
}
