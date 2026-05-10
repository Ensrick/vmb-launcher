using System.Diagnostics;
using System.IO;
using System.Text;

namespace VmbLauncher.Services;

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

public static class ProcessRunner
{
    /// <summary>Run a process, streaming each output line via <paramref name="onLine"/>. Returns the exit code and accumulated output.</summary>
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> args,
        string? workingDir,
        Action<string>? onLine,
        string? stdinInput = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinInput != null,
            CreateNoWindow = true,
            WorkingDirectory = workingDir ?? Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var sbOut = new StringBuilder();
        var sbErr = new StringBuilder();

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            sbOut.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            sbErr.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };

        if (!proc.Start()) throw new InvalidOperationException($"Failed to start: {fileName}");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (stdinInput != null)
        {
            await proc.StandardInput.WriteAsync(stdinInput);
            proc.StandardInput.Close();
        }

        using var reg = ct.Register(() => { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } });
        await proc.WaitForExitAsync(ct);

        return new ProcessResult(proc.ExitCode, sbOut.ToString(), sbErr.ToString());
    }

    /// <summary>Run a tool with "y\n" piped to its stdin. Used for ugc_tool's EULA prompt.</summary>
    /// <remarks>Originally we used `cmd /c "echo y | tool ..."` to avoid PowerShell's flaky native pipe.
    /// That had its own quoting hell with spaces in paths. Plain .NET Process redirection works fine —
    /// the PowerShell-pipe-doesn't-forward-stdin issue is a PowerShell problem, not a .NET problem.</remarks>
    public static Task<ProcessResult> RunWithEulaYesAsync(string toolPath, IEnumerable<string> toolArgs, Action<string>? onLine, CancellationToken ct = default)
        => RunAsync(toolPath, toolArgs, System.IO.Path.GetDirectoryName(toolPath), onLine, "y\n", ct);
}
