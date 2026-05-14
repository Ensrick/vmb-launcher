using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using VmbLauncher.Cli;

namespace VmbLauncher;

/// <summary>
/// Dual-mode entry point. Compiled as a console subsystem binary (OutputType=Exe) so
/// stdout/stderr are real pipes that PowerShell/cmd can capture and redirect. The GUI
/// path immediately detaches from any inherited console (FreeConsole) before any WPF
/// code runs, so the brief console flash from explorer.exe is dismissed.
///
/// Branching:
///   no args / --gui             -> WPF GUI (FreeConsole first)
///   any non-flag first arg      -> headless CLI handler (Console.* streams stay live)
///
/// Exit codes (headless only):
///   0  success
///   1  command failed (build/upload/etc returned not-ok, or runtime error)
///   2  bad usage (missing args, unknown verb)
///   3  preflight failed (settings/diagnostics blocking the requested action)
/// </summary>
public static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    [STAThread]
    public static int Main(string[] args)
    {
        if (IsHeadlessInvocation(args))
        {
            return RunHeadless(args);
        }

        // GUI path. Drop the inherited console FIRST so the brief stub window from
        // explorer.exe / shell launches is dismissed before WPF starts spinning up.
        try { FreeConsole(); } catch { /* best-effort */ }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private static bool IsHeadlessInvocation(string[] args)
    {
        // GUI mode: no args (typical double-click), or explicit --gui anywhere.
        if (args.Length == 0) return false;
        foreach (var a in args)
        {
            if (a.Equals("--gui", StringComparison.OrdinalIgnoreCase)) return false;
        }
        // Anything else (verb, flag-only, garbled args) goes through CLI so the user gets a
        // helpful "missing verb" error instead of a silently-launched GUI window.
        return true;
    }

    private static int RunHeadless(string[] args)
    {
        // Make Unicode in console output (mod descriptions, BBCode-stripped titles) survive
        // the trip to non-UTF-8 terminals.
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch { /* on rare terminal/redirect combinations this throws; harmless to skip */ }

        try
        {
            return CliDispatcher.Run(args);
        }
        catch (IOException ex) when (IsBrokenPipe(ex))
        {
            // Consumer closed stdout early — e.g. `vmblauncher list | Select -First 5` or
            // `vmblauncher list | head -3`. Unix-style behaviour says this is success; the
            // truncation is the consumer's choice, not a failure. Exit 0 so $LASTEXITCODE
            // stays clean for the surrounding pipeline.
            return CliDispatcher.ExitOk;
        }
        catch (Exception ex)
        {
            try { Console.Error.WriteLine($"vmblauncher: unhandled exception: {ex.Message}"); } catch { }
            try { Console.Error.WriteLine(ex.StackTrace); } catch { }
            return 1;
        }
        finally
        {
            try { Console.Out.Flush(); } catch { }
            try { Console.Error.Flush(); } catch { }
        }
    }

    /// <summary>
    /// True if the IOException is a broken-pipe error from stdout/stderr being closed by
    /// the consumer. Windows reports this as either ERROR_NO_DATA (232) — "the pipe is
    /// being closed" — or ERROR_BROKEN_PIPE (109). On non-Windows it's typically EPIPE (32).
    /// </summary>
    private static bool IsBrokenPipe(IOException ex)
    {
        var hr = ex.HResult & 0xFFFF;
        return hr == 109 || hr == 232 || hr == 32;
    }
}
