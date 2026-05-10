using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using VmbLauncher.Services;

namespace VmbLauncher.Tests;

public class VmbDownloaderTests
{
    [Fact]
    public void ExtractZipAssetUrl_returns_zip_url_from_real_response_shape()
    {
        var json = """
        {
          "tag_name": "1.8.4",
          "assets": [
            { "name": "vmb-1.8.4.zip", "browser_download_url": "https://example.com/vmb-1.8.4.zip" }
          ]
        }
        """;
        Assert.Equal("https://example.com/vmb-1.8.4.zip", VmbDownloader.ExtractZipAssetUrl(json));
    }

    [Fact]
    public void ExtractZipAssetUrl_skips_non_zip_assets()
    {
        var json = """
        {
          "assets": [
            { "name": "vmb-1.8.4.tar.gz", "browser_download_url": "https://example.com/tarball" },
            { "name": "vmb-1.8.4.zip", "browser_download_url": "https://example.com/zip" }
          ]
        }
        """;
        Assert.Equal("https://example.com/zip", VmbDownloader.ExtractZipAssetUrl(json));
    }

    [Fact]
    public void ExtractZipAssetUrl_returns_null_when_no_assets()
    {
        var json = """{ "tag_name": "x", "assets": [] }""";
        Assert.Null(VmbDownloader.ExtractZipAssetUrl(json));
    }

    [Fact]
    public void ExtractZipAssetUrl_returns_null_when_no_zip_asset()
    {
        var json = """
        {
          "assets": [
            { "name": "source.tar.gz", "browser_download_url": "https://example.com/src" }
          ]
        }
        """;
        Assert.Null(VmbDownloader.ExtractZipAssetUrl(json));
    }

    [Fact]
    public void ExtractZipAssetUrl_handles_assets_field_missing()
    {
        var json = "{ \"tag_name\": \"x\" }";
        Assert.Null(VmbDownloader.ExtractZipAssetUrl(json));
    }

    [Fact]
    public async Task DownloadAndInstall_extracts_zip_to_install_dir()
    {
        using var t = new TempDir();
        // Build a fake VMB zip with a vmb.exe inside.
        var fakeZipPath = Path.Combine(t.Path, "fake-vmb.zip");
        using (var fs = File.Create(fakeZipPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("vmb.exe");
            using var es = entry.Open();
            es.Write(new byte[] { 0x4D, 0x5A }); // MZ
        }
        var zipBytes = File.ReadAllBytes(fakeZipPath);

        var apiJson = """
        {
          "assets": [
            { "name": "vmb-fake.zip", "browser_download_url": "https://fake.test/vmb.zip" }
          ]
        }
        """;

        var handler = new ScriptedHandler(req =>
        {
            if (req.RequestUri!.ToString() == VmbDownloader.GithubApiUrl)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(apiJson) };
            if (req.RequestUri.ToString() == "https://fake.test/vmb.zip")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var installDir = Path.Combine(t.Path, "install");
        var dl = new VmbDownloader(new HttpClient(handler), installDir);

        var progressEvents = new List<DownloadProgress>();
        var progress = new Progress<DownloadProgress>(p => progressEvents.Add(p));
        var result = await dl.DownloadAndInstallAsync(progress);

        Assert.True(result.Ok, $"Install failed: {result.Message}");
        Assert.Equal(installDir, result.VmbRoot);
        Assert.True(File.Exists(Path.Combine(installDir, "vmb.exe")));
        Assert.NotEmpty(progressEvents);
    }

    [Fact]
    public async Task DownloadAndInstall_fails_clean_when_no_zip_asset()
    {
        using var t = new TempDir();
        var apiJson = "{ \"assets\": [] }";

        var handler = new ScriptedHandler(req =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(apiJson) });

        var dl = new VmbDownloader(new HttpClient(handler), Path.Combine(t.Path, "install"));
        var result = await dl.DownloadAndInstallAsync(progress: null);

        Assert.False(result.Ok);
        Assert.Contains("zip", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DownloadAndInstall_fails_when_download_returns_404()
    {
        using var t = new TempDir();
        var apiJson = """
        {
          "assets": [
            { "name": "vmb.zip", "browser_download_url": "https://fake.test/missing.zip" }
          ]
        }
        """;
        var handler = new ScriptedHandler(req =>
        {
            if (req.RequestUri!.ToString() == VmbDownloader.GithubApiUrl)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(apiJson) };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var dl = new VmbDownloader(new HttpClient(handler), Path.Combine(t.Path, "install"));
        var result = await dl.DownloadAndInstallAsync(progress: null);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task DownloadAndInstall_fails_when_zip_missing_vmbexe()
    {
        using var t = new TempDir();
        // Zip without vmb.exe.
        var fakeZipPath = Path.Combine(t.Path, "no-exe.zip");
        using (var fs = File.Create(fakeZipPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("readme.txt");
            using var es = entry.Open();
            es.Write(System.Text.Encoding.UTF8.GetBytes("not a real vmb"));
        }
        var zipBytes = File.ReadAllBytes(fakeZipPath);

        var apiJson = """
        {
          "assets": [
            { "name": "vmb.zip", "browser_download_url": "https://fake.test/vmb.zip" }
          ]
        }
        """;
        var handler = new ScriptedHandler(req =>
        {
            if (req.RequestUri!.ToString() == VmbDownloader.GithubApiUrl)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(apiJson) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) };
        });

        var dl = new VmbDownloader(new HttpClient(handler), Path.Combine(t.Path, "install"));
        var result = await dl.DownloadAndInstallAsync(progress: null);

        Assert.False(result.Ok);
        Assert.Contains("vmb.exe", result.Message);
    }

    [Fact]
    public void DefaultInstallDir_lives_under_localappdata()
    {
        var d = VmbDownloader.DefaultInstallDir();
        var lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(lad, d);
        Assert.EndsWith("vmb", d);
    }

    [Fact]
    public void ClearVmbInstallDir_wipes_existing_contents()
    {
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Path, "old.exe"), "old");
        Directory.CreateDirectory(Path.Combine(t.Path, "subdir"));
        File.WriteAllText(Path.Combine(t.Path, "subdir", "x.txt"), "x");

        VmbDownloader.ClearVmbInstallDir(t.Path);

        Assert.Empty(Directory.EnumerateFileSystemEntries(t.Path));
    }

    [Fact]
    public void ClearVmbInstallDir_no_throw_on_missing_dir()
    {
        var fake = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        VmbDownloader.ClearVmbInstallDir(fake); // must not throw
    }

    /// <summary>Test-only HttpMessageHandler that runs a function for each request.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _f;
        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> f) { _f = f; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_f(request));
    }
}
