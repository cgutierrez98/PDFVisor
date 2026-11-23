using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace PDFVisor.Core;

public sealed record UpdateInfo(
    Version LatestVersion,
    Version CurrentVersion,
    string TagName,
    string ReleaseUrl,
    string? InstallerDownloadUrl,
    string? InstallerFileName,
    string ReleaseNotes)
{
    public bool IsUpdateAvailable => LatestVersion > CurrentVersion;
}

public static class UpdateService
{
    public const string DefaultOwner = "cgutierrez98";
    public const string DefaultRepo = "PDFVisor";

    public static Version GetCurrentVersion()
    {
        var v = Assembly.GetEntryAssembly()?.GetName().Version
                ?? typeof(UpdateService).Assembly.GetName().Version
                ?? new Version(0, 9, 0);
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    public static async Task<UpdateInfo?> CheckForUpdatesAsync(
        string owner = DefaultOwner,
        string repo = DefaultRepo,
        CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PDFVisor", GetCurrentVersion().ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        string url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;

        string json = await response.Content.ReadAsStringAsync(ct);
        return ParseGitHubReleaseJson(json, GetCurrentVersion());
    }

    public static UpdateInfo? ParseGitHubReleaseJson(string json, Version currentVersion)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("tag_name", out var tagProp)) return null;
        string tag = tagProp.GetString() ?? string.Empty;
        if (!TryParseSemanticVersion(tag, out var latest)) return null;

        string releaseUrl = root.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() ?? "" : "";
        string notes = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";

        string? installerUrl = null;
        string? installerName = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string dl = asset.TryGetProperty("browser_download_url", out var d) ? d.GetString() ?? "" : "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(dl))
                {
                    installerName = name;
                    installerUrl = dl;
                    break;
                }
            }
        }

        return new UpdateInfo(
            latest,
            new Version(currentVersion.Major, currentVersion.Minor, Math.Max(0, currentVersion.Build)),
            tag,
            releaseUrl,
            installerUrl,
            installerName,
            notes);
    }

    public static bool TryParseSemanticVersion(string tagOrVersion, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tagOrVersion)) return false;
        string clean = tagOrVersion.Trim().TrimStart('v', 'V');
        int dash = clean.IndexOf('-');
        if (dash >= 0) clean = clean[..dash];
        if (!Version.TryParse(clean, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        return true;
    }

    public static async Task<string> DownloadInstallerAsync(
        UpdateInfo update,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(update.InstallerDownloadUrl))
            throw new InvalidOperationException("Esta release no contiene un instalador .exe descargable.");

        string dir = Path.Combine(Path.GetTempPath(), "PDFVisor-Updates");
        Directory.CreateDirectory(dir);
        string fileName = update.InstallerFileName ?? $"PDFVisor-Setup-{update.LatestVersion}.exe";
        string destPath = Path.Combine(dir, fileName);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PDFVisor", GetCurrentVersion().ToString()));

        using var resp = await http.GetAsync(update.InstallerDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        long? totalBytes = resp.Content.Headers.ContentLength;
        await using var input = await resp.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(destPath);

        byte[] buffer = new byte[81920];
        long downloaded = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;
            if (totalBytes is > 0)
            {
                progress?.Report((double)downloaded / totalBytes.Value);
            }
        }

        progress?.Report(1.0);
        return destPath;
    }
}
