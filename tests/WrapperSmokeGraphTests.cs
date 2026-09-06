using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace VmbLauncher.Tests;

public sealed class WrapperSmokeGraphTests
{
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Theory]
    [InlineData("powershell.exe", false)]
    [InlineData("powershell.exe", true)]
    [InlineData("pwsh.exe", false)]
    [InlineData("pwsh.exe", true)]
    public void NoninteractiveGuardAndPlantedFailuresRunWithoutExplicitRoot(string shell, bool selfTest)
    {
        var arguments = new List<string> { "-NoLogo", "-NoProfile", "-NonInteractive", "-File",
            Path.Combine(Root, "tests", "check_noninteractive_contract.ps1") };
        if (selfTest) arguments.Add("-SelfTest");
        var result = Run(shell, arguments.ToArray());
        Assert.True(result.Code == 0, result.Output);
        Assert.Contains(selfTest ? "SELFTEST OK" : "OK -- default verification", result.Output);
    }

    [Theory]
    [InlineData("powershell.exe", "success")]
    [InlineData("powershell.exe", "test-failure")]
    [InlineData("powershell.exe", "missing-output")]
    [InlineData("pwsh.exe", "success")]
    [InlineData("pwsh.exe", "test-failure")]
    [InlineData("pwsh.exe", "missing-output")]
    public void CanonicalTestScriptRequiresFreshTestGraphOutput(string shell, string mode)
    {
        using var tmp = new TempDir();
        // Execute the real orchestration script in an empty synthetic checkout.
        // Only external test/guard/smoke boundaries are replaced, not its control flow.
        tmp.Write("test.ps1", File.ReadAllText(Path.Combine(Root, "test.ps1")));
        tmp.Write("tests/check_noninteractive_contract.ps1", "Add-Content -LiteralPath $env:WRAPPER_TRACE -Value 'guard'; exit 0");
        tmp.Write("tests/transaction_wrapper_smoke.ps1", """
            param([string]$Exe)
            $expected = Join-Path (Split-Path $PSScriptRoot -Parent) 'bin\TestHooks\Debug\net9.0-windows\VMBLauncher.exe'
            if ($Exe -cne $expected -or -not (Test-Path -LiteralPath $Exe -PathType Leaf)) {
                throw 'wrapper did not receive the exact newly built TestHooks executable'
            }
            Add-Content -LiteralPath $env:WRAPPER_TRACE -Value 'smoke'
            exit 0
            """);
        var runner = tmp.Write("run.ps1", """
            param([string]$Mode)
            $ErrorActionPreference = 'Stop'
            $env:WRAPPER_TRACE = Join-Path $PSScriptRoot 'trace.txt'
            function dotnet {
                if ($args.Count -ne 5 -or $args[0] -cne 'test' -or $args[2] -cne '-c' -or
                    $args[3] -cne 'Debug' -or $args[4] -cne '--nologo' -or
                    $args[1] -cne (Join-Path $PSScriptRoot 'tests\VmbLauncher.Tests.csproj')) {
                    throw 'test graph must explicitly select Debug'
                }
                Add-Content -LiteralPath $env:WRAPPER_TRACE -Value 'test'
                if ($Mode -eq 'test-failure') { $global:LASTEXITCODE = 7; return }
                if ($Mode -eq 'success') {
                    $exe = Join-Path $PSScriptRoot 'bin\TestHooks\Debug\net9.0-windows\VMBLauncher.exe'
                    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($exe)) | Out-Null
                    [IO.File]::WriteAllText($exe, 'external-build-boundary-fixture; never executed')
                }
                $global:LASTEXITCODE = 0
            }
            try { & (Join-Path $PSScriptRoot 'test.ps1'); exit 0 }
            catch { [Console]::Error.WriteLine($_.Exception.Message); exit 9 }
            """);
        Assert.False(Directory.Exists(Path.Combine(tmp.Path, "bin", "Debug")));
        var result = Run(shell, "-NoLogo", "-NoProfile", "-NonInteractive", "-File", runner, mode);
        var trace = File.ReadAllLines(Path.Combine(tmp.Path, "trace.txt"));
        Assert.False(Directory.Exists(Path.Combine(tmp.Path, "bin", "Debug")));
        if (mode == "success")
        {
            Assert.True(result.Code == 0, result.Output);
            Assert.Equal(new[] { "guard", "test", "smoke" }, trace);
        }
        else
        {
            Assert.Equal(9, result.Code);
            Assert.Equal(new[] { "guard", "test" }, trace);
            Assert.Contains(mode == "test-failure" ? "Tests failed (exit code 7)" :
                "Test graph did not produce the wrapper fixture executable", result.Output);
        }
    }

    [Theory]
    [InlineData("Debug")]
    [InlineData("Release")]
    public void EvaluatedProductionAndTestGraphsKeepMutexHooksIsolated(string configuration)
    {
        foreach (var fixture in new[] { false, true })
        {
            var arguments = new List<string> { "msbuild", Path.Combine(Root, "VmbLauncher.csproj"),
                "--nologo", "-p:Configuration=" + configuration,
                "-getProperty:DefineConstants,OutputPath,IntermediateOutputPath" };
            // Normal production invocation has NO fixture property at all.
            if (fixture) arguments.Add("-p:VmbLauncherTestBuild=true");
            var evaluation = Run("dotnet.exe", arguments.ToArray());
            Assert.True(evaluation.Code == 0, evaluation.Output);
            using var json = JsonDocument.Parse(evaluation.Output);
            var properties = json.RootElement.GetProperty("Properties");
            var symbols = properties.GetProperty("DefineConstants").GetString()!
                .Split(';', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(fixture, symbols.Contains("VMBLAUNCHER_TEST_HOOKS", StringComparer.Ordinal));
            foreach (var property in new[] { "OutputPath", "IntermediateOutputPath" })
            {
                var path = properties.GetProperty(property).GetString()!.Replace('\\', '/');
                Assert.Equal(fixture, path.Contains("/TestHooks/", StringComparison.Ordinal));
                Assert.Contains("/" + configuration + "/", path);
            }
            // Parse actual source with evaluated compiler symbols, not an assumed
            // Debug=fixture equivalence. Production cannot read the test mutex env.
            var syntax = CSharpSyntaxTree.ParseText(
                File.ReadAllText(Path.Combine(Root, "Services", "MachineTransactionLease.cs")),
                new CSharpParseOptions(preprocessorSymbols: symbols)).GetRoot();
            var resolver = syntax.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(method => method.Identifier.ValueText == "ResolveMutexName");
            var testEnvironmentReads = resolver.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(call => call.Expression.ToString() == "Environment.GetEnvironmentVariable").ToArray();
            Assert.Equal(fixture ? 2 : 0, testEnvironmentReads.Length);
        }
    }

    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    public void WrapperRefusesProductionOrForeignExecutableBeforeFixtureEffects(string shell)
    {
        using var tmp = new TempDir();
        var sentinel = tmp.Write("VMBLauncher.exe", "must never execute");
        var result = Run(shell, "-NoLogo", "-NoProfile", "-NonInteractive", "-File",
            Path.Combine(Root, "tests", "transaction_wrapper_smoke.ps1"), "-Exe", sentinel);
        Assert.Equal(2, result.Code);
        Assert.Contains("expected an existing isolated TestHooks Debug/Release executable", result.Output);
        Assert.Equal("must never execute", File.ReadAllText(sentinel));
    }

    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    public void RealWrapperRestoresPrivateEnvironmentAndCleansOnlyItsFixture(string shell)
    {
        using var tmp = new TempDir();
        var configuration = typeof(WrapperSmokeGraphTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var executable = Path.Combine(Root, "bin", "TestHooks", configuration,
            "net9.0-windows", "VMBLauncher.exe");
        var runner = tmp.Write("run-wrapper.ps1", """
            param([string]$Wrapper, [string]$Exe)
            $ErrorActionPreference = 'Stop'
            # Only this child process receives fixture environment; real settings
            # and the application's normal global transaction are never selected.
            $env:TEMP = $PSScriptRoot
            $env:TMP = $PSScriptRoot
            $fields = @('LEASE_ID', 'OWNER_PID', 'OWNER_START_UTC_TICKS', 'RECORD_PATH', 'TEST_MODE', 'TEST_MUTEX_NAME')
            foreach ($field in $fields) {
                [Environment]::SetEnvironmentVariable('VMBLAUNCHER_TRANSACTION_' + $field, 'restore-' + $field)
            }
            & $Wrapper -Exe $Exe
            if ($LASTEXITCODE -ne 0) { throw 'actual wrapper smoke failed' }
            foreach ($field in $fields) {
                if ([Environment]::GetEnvironmentVariable('VMBLAUNCHER_TRANSACTION_' + $field) -cne ('restore-' + $field)) {
                    throw ('wrapper leaked its private environment: ' + $field)
                }
            }
            if (@([IO.Directory]::EnumerateDirectories($PSScriptRoot, 'vmb-wrapper-join-*')).Count -ne 0) {
                throw 'wrapper did not remove its owned temporary fixture'
            }
            Write-Output 'WRAPPER_ENVIRONMENT_RESTORED_AND_FIXTURE_REMOVED'
            exit 0
            """);
        var result = Run(shell, "-NoLogo", "-NoProfile", "-NonInteractive", "-File", runner,
            Path.Combine(Root, "tests", "transaction_wrapper_smoke.ps1"), executable);
        Assert.True(result.Code == 0, result.Output);
        Assert.Contains("real launcher joined its exact live parent; fake VMB only", result.Output);
        Assert.Contains("WRAPPER_ENVIRONMENT_RESTORED_AND_FIXTURE_REMOVED", result.Output);
    }

    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    public void HostedDiscoverySetupPassesRealReadOnlyCliAndMissingMarkerStillFails(string shell)
    {
        using var tmp = new TempDir();
        // Run the exact hosted setup body, not a separately authored equivalent.
        var workflow = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "qa.yml"));
        var start = workflow.IndexOf("      - name: Configure hermetic headless discovery fixture", StringComparison.Ordinal);
        Assert.True(start >= 0);
        start = workflow.IndexOf("        run: |", start, StringComparison.Ordinal);
        Assert.True(start >= 0);
        start = workflow.IndexOf('\n', start) + 1;
        var end = workflow.IndexOf("      - name: Release test graph", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var setupBody = string.Join("\n", workflow[start..end].Split('\n').Select(line =>
            string.IsNullOrWhiteSpace(line) ? "" : line[10..].TrimEnd('\r')));
        var setup = tmp.Write("setup.ps1", setupBody);
        var runnerTemp = tmp.CreateSubdir("runner");
        var githubEnv = tmp.Write("github-env.txt", "");
        var environment = new Dictionary<string, string>
        {
            ["RUNNER_TEMP"] = runnerTemp,
            ["GITHUB_ENV"] = githubEnv,
        };
        var setupResult = RunWithEnvironment(shell, environment,
            "-NoLogo", "-NoProfile", "-NonInteractive", "-File", setup);
        Assert.True(setupResult.Code == 0, setupResult.Output);
        var config = Path.Combine(runnerTemp, "vmb-launcher-appdata", "VMBLauncher", "settings.json");
        using var settings = JsonDocument.Parse(File.ReadAllText(config));
        var vmbRoot = settings.RootElement.GetProperty("VmbRoot").GetString()!;
        Assert.Equal(Path.Combine(runnerTemp, "vmb-launcher-discovery-only"), vmbRoot);
        var marker = Path.Combine(vmbRoot, "vmb.exe");
        var markerBytes = File.ReadAllBytes(marker);
        Assert.Equal("DISCOVERY ONLY - NOT EXECUTABLE - MUST NEVER BE RUN", System.Text.Encoding.UTF8.GetString(markerBytes));
        Assert.Contains("APPDATA=" + Path.Combine(runnerTemp, "vmb-launcher-appdata"), File.ReadAllText(githubEnv));
        var before = Snapshot(tmp.Path);
        var defaultConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VMBLauncher", "settings.json");
        var defaultBefore = File.Exists(defaultConfig) ? File.ReadAllBytes(defaultConfig) : null;
        var configuration = typeof(WrapperSmokeGraphTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var executable = Path.Combine(Root, "bin", "TestHooks", configuration, "net9.0-windows", "VMBLauncher.exe");

        // Explicit --config selects only the hosted fixture, never machine defaults.
        var list = Run(executable, "--config", config, "--no-banner", "list");
        Assert.True(list.Code == 0, list.Output);
        Assert.Matches(@"NAME\s+VISIBILITY\s+WORKSHOP_ID\s+BUILT", list.Output);
        Assert.Matches(@"(?m)^general_tweaker\s+private\s+1\s+no build", list.Output);
        Assert.Matches(@"(?m)^chaos_wastes_tweaker\s+public\s+2\s+no build", list.Output);
        foreach (var mod in new[] { "general_tweaker", "chaos_wastes_tweaker" })
        {
            var info = Run(executable, "info", mod, "--no-banner", "--config", config);
            Assert.True(info.Code == 0, info.Output);
            Assert.Contains("Visibility:", info.Output);
            Assert.Contains("Workshop ID:", info.Output);
            Assert.Contains(Path.Combine(runnerTemp, "vmb-launcher-project", mod), info.Output);
        }
        Assert.Equal(2, Run(executable, "info", "no_such_mod_12345", "--no-banner", "--config", config).Code);
        AssertSnapshot(before, tmp.Path);

        // Independent negative: a configured nonempty root disables autodetect,
        // but an existing empty directory is NOT a valid VMB installation.
        File.Delete(marker);
        foreach (var arguments in new[] { new[] { "list" }, new[] { "info", "general_tweaker" }, new[] { "info", "no_such_mod_12345" } })
        {
            var rejected = Run(executable, arguments.Concat(new[] { "--no-banner", "--config", config }).ToArray());
            Assert.Equal(3, rejected.Code);
            Assert.Contains("VMB", rejected.Output);
            Assert.Contains("isn't configured", rejected.Output);
        }
        Assert.False(File.Exists(marker));
        File.WriteAllBytes(marker, markerBytes);
        AssertSnapshot(before, tmp.Path);
        Assert.Equal(defaultBefore, File.Exists(defaultConfig) ? File.ReadAllBytes(defaultConfig) : null);
    }

    private static Dictionary<string, byte[]> Snapshot(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllBytes, StringComparer.Ordinal);

    private static void AssertSnapshot(Dictionary<string, byte[]> expected, string root)
    {
        var actual = Snapshot(root);
        Assert.Equal(expected.Keys.OrderBy(path => path, StringComparer.Ordinal), actual.Keys.OrderBy(path => path, StringComparer.Ordinal));
        foreach (var (path, bytes) in expected) Assert.Equal(bytes, actual[path]);
    }

    private static (int Code, string Output) Run(string executable, params string[] args)
        => RunWithEnvironment(executable, null, args);

    private static (int Code, string Output) RunWithEnvironment(string executable, Dictionary<string, string>? environment, params string[] args)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Root,
        };
        if (environment is not null)
            foreach (var (name, value) in environment) start.Environment[name] = value;
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException("Bounded script/evaluation fixture did not exit.");
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }
}
