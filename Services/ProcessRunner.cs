using System.Diagnostics;
using System.IO;
using System.Text;

namespace VmbLauncher.Services;

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

public static class ProcessRunner
{
    /// <summary>
    /// Run a process, streaming each output line via <paramref name="onLine"/>. Returns the exit code and accumulated output.
    /// </summary>
    /// <remarks>
    /// We deliberately do NOT use <see cref="Process.BeginOutputReadLine"/> /
    /// <see cref="Process.BeginErrorReadLine"/>. Those treat bare <c>\r</c> as a line terminator,
    /// which produces the "[C\nompiler]" / one-char-per-line corruption when the Stingray compiler
    /// emits <c>\r</c> inside its output (progress overwrites, ANSI-style status lines). We also
    /// need stdout/stderr to be serialised through a shared lock so per-line callbacks never
    /// interleave at sub-line granularity.
    ///
    /// Strategy: read both streams as async character batches, split only on <c>\n</c>, strip
    /// trailing <c>\r</c> on completed lines (CRLF support), and call <paramref name="onLine"/>
    /// under a single lock so stdout and stderr stay ordered relative to each other.
    /// </remarks>
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
        if (!proc.Start()) throw new InvalidOperationException($"Failed to start: {fileName}");

        // Single lock guards both the per-line callback and the Stdout/Stderr accumulators.
        // Without it, stdout and stderr can interleave inside an onLine call, producing scrambled
        // log output in the UI.
        var sync = new object();
        void EmitLine(string line, StringBuilder accumulator)
        {
            lock (sync)
            {
                accumulator.AppendLine(line);
                onLine?.Invoke(line);
            }
        }

        var stdoutTask = ReadLinesAsync(proc.StandardOutput, line => EmitLine(line, sbOut), ct);
        var stderrTask = ReadLinesAsync(proc.StandardError, line => EmitLine(line, sbErr), ct);

        if (stdinInput != null)
        {
            try
            {
                await proc.StandardInput.WriteAsync(stdinInput);
                proc.StandardInput.Close();
            }
            catch (IOException) { /* process may have exited before reading stdin */ }
        }

        using var reg = ct.Register(() => { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } });

        // Wait for both readers to drain their pipes AND the process to exit. Order matters:
        // process exit doesn't guarantee the pipes have been drained yet.
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask);
        }
        catch (OperationCanceledException) { /* propagated below if ct cancelled */ }
        await proc.WaitForExitAsync(ct);

        return new ProcessResult(proc.ExitCode, sbOut.ToString(), sbErr.ToString());
    }

    /// <summary>
    /// Read <paramref name="reader"/> a chunk at a time, emitting lines via <paramref name="onLine"/>
    /// every time we encounter <c>\n</c>. Bare <c>\r</c> is preserved inside the buffer (NOT a line
    /// terminator). Trailing <c>\r</c> on a completed line is stripped (handles CRLF).
    /// </summary>
    public static async Task ReadLinesAsync(StreamReader reader, Action<string> onLine, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new char[4096];
        while (true)
        {
            int n;
            try { n = await reader.ReadAsync(buf, ct); }
            catch (OperationCanceledException) { break; }
            if (n == 0) break;
            for (int i = 0; i < n; i++)
            {
                var c = buf[i];
                if (c == '\n')
                {
                    var line = sb.ToString();
                    if (line.Length > 0 && line[^1] == '\r') line = line.Substring(0, line.Length - 1);
                    sb.Clear();
                    onLine(line);
                }
                else
                {
                    sb.Append(c);
                }
            }
        }
        if (sb.Length > 0)
        {
            var trailing = sb.ToString();
            if (trailing.Length > 0 && trailing[^1] == '\r') trailing = trailing.Substring(0, trailing.Length - 1);
            onLine(trailing);
        }
    }

    /// <summary>Run a tool with "y\n" piped to its stdin. Used for ugc_tool's EULA prompt.</summary>
    public static Task<ProcessResult> RunWithEulaYesAsync(string toolPath, IEnumerable<string> toolArgs, Action<string>? onLine, CancellationToken ct = default)
        => RunAsync(toolPath, toolArgs, System.IO.Path.GetDirectoryName(toolPath), onLine, "y\n", ct);
}
