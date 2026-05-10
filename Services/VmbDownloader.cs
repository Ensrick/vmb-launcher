using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VmbLauncher.Services;

public sealed record DownloadProgress(long BytesDownloaded, long? TotalBytes, string Message)
{
    public double? Fraction => TotalBytes is > 0 ? (double)BytesDownloaded / TotalBytes : null;
}

public sealed record InstallResult(bool Ok, string Message, string? VmbRoot = null);

public sealed class VmbDownloader
{
    public const string GithubApiUrl = "https://api.github.com/repos/Vermintide-Mod-Framework/Vermintide-Mod-Builder/releases/latest";
    public const string UserAgent = "VMBLauncher/0.2";

    private readonly HttpClient _http;
    public string InstallDir { get; }

    public VmbDownloader(HttpClient? http = null, string? installDir = null)
    {
        _http = http ?? CreateDefaultClient();
        InstallDir = installDir ?? DefaultInstallDir();
    }

    public static string DefaultInstallDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VMBLauncher", "vmb");

    public static HttpClient CreateDefaultClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public async Task<string?> FindLatestZipUrlAsync(CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync(GithubApiUrl, ct);
        return ExtractZipAssetUrl(json);
    }

    /// <summary>Pure parsing of the GitHub releases JSON. Extracted for unit testing.</summary>
    public static string? ExtractZipAssetUrl(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var name)) continue;
            var n = name.GetString();
            if (n == null || !n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            if (asset.TryGetProperty("browser_download_url", out var url))
                return url.GetString();
        }
        return null;
    }

    public async Task<InstallResult> DownloadAndInstallAsync(IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        try
        {
            progress?.Report(new(0, null, "Looking up latest VMB release..."));
            var zipUrl = await FindLatestZipUrlAsync(ct);
            if (zipUrl == null)
                return new InstallResult(false, "Could not find a .zip asset on the latest VMB release.");

            // Download to a temp file so a partial download never lands at the install dir.
            var tempZip = Path.Combine(Path.GetTempPath(), $"vmb-{Guid.NewGuid():N}.zip");
            try
            {
                progress?.Report(new(0, null, $"Downloading {Path.GetFileName(zipUrl)}..."));
                await DownloadFileAsync(zipUrl, tempZip, progress, ct);

                progress?.Report(new(0, null, "Extracting..."));
                Directory.CreateDirectory(InstallDir);
                ClearVmbInstallDir(InstallDir);
                ZipFile.ExtractToDirectory(tempZip, InstallDir, overwriteFiles: true);

                if (!File.Exists(Path.Combine(InstallDir, "vmb.exe")))
                    return new InstallResult(false, $"Extracted but vmb.exe not at expected location ({InstallDir}\\vmb.exe).");

                progress?.Report(new(0, null, "Done"));
                return new InstallResult(true, $"VMB installed at {InstallDir}", InstallDir);
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            }
        }
        catch (OperationCanceledException)
        {
            return new InstallResult(false, "Cancelled.");
        }
        catch (Exception ex)
        {
            return new InstallResult(false, ex.Message);
        }
    }

    /// <summary>Wipe known VMB files in the install dir so a re-install isn't shadowed by stale artifacts. Preserves nothing — VMB has no per-user state at the install dir.</summary>
    public static void ClearVmbInstallDir(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            try
            {
                if (File.Exists(entry)) File.Delete(entry);
                else if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
            }
            catch
            {
                // Best-effort: a process holding a handle on an old vmb.exe would cause this to throw.
                // Surface that as a download error during ZipFile.ExtractToDirectory.
            }
        }
    }

    private async Task DownloadFileAsync(string url, string destPath, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;

        using var src = await resp.Content.ReadAsStreamAsync(ct);
        using var dst = File.Create(destPath);
        var buffer = new byte[81920];
        long downloaded = 0;
        long lastReport = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;
            if (downloaded - lastReport > 256 * 1024) // throttle: report every 256 KB
            {
                lastReport = downloaded;
                progress?.Report(new(downloaded, total, FormatProgress(downloaded, total)));
            }
        }
        progress?.Report(new(downloaded, total, FormatProgress(downloaded, total)));
    }

    private static string FormatProgress(long downloaded, long? total)
    {
        var dlMb = downloaded / 1024.0 / 1024.0;
        if (total is > 0)
        {
            var tot = total.Value / 1024.0 / 1024.0;
            return $"{dlMb:F1} / {tot:F1} MB";
        }
        return $"{dlMb:F1} MB";
    }
}
