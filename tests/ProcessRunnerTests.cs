using VmbLauncher.Services;

namespace VmbLauncher.Tests;

public class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_captures_stdout()
    {
        var lines = new List<string>();
        var result = await ProcessRunner.RunAsync(
            "cmd.exe",
            new[] { "/c", "echo hello-from-test" },
            null,
            l => lines.Add(l));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(lines, l => l.Contains("hello-from-test"));
    }

    [Fact]
    public async Task RunAsync_returns_nonzero_exitcode()
    {
        var result = await ProcessRunner.RunAsync(
            "cmd.exe",
            new[] { "/c", "exit /b 7" },
            null,
            null);
        Assert.Equal(7, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_pipes_stdin_when_provided()
    {
        // findstr "." passes through every non-empty stdin line to stdout. This sidesteps cmd's
        // parse-time variable expansion, which silently no-ops `%x%` after `set /p` in a single
        // command line.
        var result = await ProcessRunner.RunAsync(
            "findstr.exe",
            new[] { "." },
            null,
            null,
            stdinInput: "hello-stdin\r\n");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello-stdin", result.Stdout);
    }

    [Fact]
    public async Task RunAsync_cancellation_kills_process()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await ProcessRunner.RunAsync("cmd.exe", new[] { "/c", "ping -n 30 127.0.0.1 >nul" }, null, null, null, cts.Token);
        }
        catch (OperationCanceledException) { /* expected */ }
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Process should be killed within a few seconds, but took {sw.Elapsed}");
    }
}
