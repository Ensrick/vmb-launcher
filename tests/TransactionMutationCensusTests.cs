using System.IO;

namespace VmbLauncher.Tests;

public class TransactionMutationCensusTests
{
    [Fact]
    public void GuiSettingsAndScaffoldingUseTransactionBoundaries()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var firstRun = File.ReadAllText(Path.Combine(root, "Views", "FirstRunWindow.xaml.cs"));
        var settings = File.ReadAllText(Path.Combine(root, "Views", "SettingsWindow.xaml.cs"));
        var main = File.ReadAllText(Path.Combine(root, "MainWindow.xaml.cs"));
        var newMod = File.ReadAllText(Path.Combine(root, "Views", "NewModWindow.xaml.cs"));
        var locator = File.ReadAllText(Path.Combine(root, "Services", "SteamLocator.cs"));
        var treeGuard = File.ReadAllText(Path.Combine(root, "Services", "ProcessTreeGuard.cs"));
        var machineLease = File.ReadAllText(Path.Combine(root, "Services", "MachineTransactionLease.cs"));
        var settingsModel = File.ReadAllText(Path.Combine(root, "Services", "Settings.cs"));
        var cli = File.ReadAllText(Path.Combine(root, "Cli", "CliDispatcher.cs"));
        var processRunner = File.ReadAllText(Path.Combine(root, "Services", "ProcessRunner.cs"));
        var receiptGate = File.ReadAllText(Path.Combine(root, "Services", "PublicationReceiptGate.cs"));
        var uploadStager = File.ReadAllText(Path.Combine(root, "Services", "UploadStager.cs"));
        var uploadLease = File.ReadAllText(Path.Combine(root, "Services", "UploadPathLease.cs"));
        var scaffolder = File.ReadAllText(Path.Combine(root, "Services", "ModScaffolder.cs"));
        var discovery = File.ReadAllText(Path.Combine(root, "Services", "ModDiscovery.cs"));
        var titleSync = File.ReadAllText(Path.Combine(root, "Services", "TitleVersionSync.cs"));
        var downloader = File.ReadAllText(Path.Combine(root, "Services", "VmbDownloader.cs"));
        var guiAction = File.ReadAllText(Path.Combine(root, "Services", "GuiActionTransaction.cs"));
        var modRunner = File.ReadAllText(Path.Combine(root, "Services", "ModRunner.cs"));

        Assert.Contains("GuiSettingsTransaction.Enter", firstRun);
        Assert.Contains("GuiSettingsTransaction.Enter", settings);
        Assert.DoesNotContain("Settings.Save()", firstRun);
        Assert.DoesNotContain("Settings.Save()", settings);
        Assert.DoesNotContain("gui-settings-save", main);
        var createStart = newMod.IndexOf("private async void BtnCreate_Click", StringComparison.Ordinal);
        var createAction = newMod.IndexOf("GuiActionTransaction.Enter", createStart, StringComparison.Ordinal);
        var createVmbResolve = newMod.IndexOf("VmbLocator.Resolve", createStart, StringComparison.Ordinal);
        Assert.True(createStart >= 0 && createAction > createStart && createAction < createVmbResolve);
        Assert.True(
            newMod.IndexOf("GuiActionTransaction.Enter", StringComparison.Ordinal) <
            newMod.IndexOf("ModScaffolder.Scaffold", StringComparison.Ordinal));
        Assert.True(
            guiAction.IndexOf("MachineTransactionLease.Enter", StringComparison.Ordinal) <
            guiAction.IndexOf("Settings.LoadForMutation(configPath)", StringComparison.Ordinal));
        Assert.True(
            guiAction.IndexOf("Settings.LoadForMutation(configPath)", StringComparison.Ordinal) <
            guiAction.IndexOf("RefineOwnedProjectRoot", StringComparison.Ordinal));
        Assert.Contains("RefineOwnedProjectRoot(project.Root)", guiAction);
        Assert.Contains("RefineOwnedProjectRoot(project.Root)", cli);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(
            modRunner, @"mod\.Name, project\.Root, L\)").Count);
        var joinStart = machineLease.IndexOf("private static LeaseState JoinWrapperLease", StringComparison.Ordinal);
        var watcherStart = machineLease.IndexOf("var watcher =", joinStart, StringComparison.Ordinal);
        var joinProbe = machineLease[joinStart..watcherStart];
        Assert.Contains("probe.Abandon()", joinProbe);
        Assert.DoesNotContain("ReleaseMutex", joinProbe);
        Assert.True(
            main.IndexOf("GuiActionTransaction.Enter", StringComparison.Ordinal) <
            main.IndexOf("new ModRunner(_settings", StringComparison.Ordinal));
        Assert.DoesNotContain("Directory.CreateDirectory", locator);
        Assert.DoesNotContain("Process.Start", firstRun);
        Assert.DoesNotContain("Process.Start", settings);
        Assert.DoesNotContain("Process.Start", main);
        Assert.Contains("NonMutationShell", firstRun);
        Assert.Contains("NonMutationShell", settings);
        Assert.Contains("NonMutationShell", main);
        Assert.Contains("StartBreakawayExplorer", treeGuard);
        Assert.Contains("Environment.SpecialFolder.Windows", treeGuard);
        Assert.Contains("fullPath, commandLine", treeGuard);
        var defaultPathStart = settingsModel.IndexOf("public static string DefaultConfigPath", StringComparison.Ordinal);
        var loadStart = settingsModel.IndexOf("public static Settings Load()", StringComparison.Ordinal);
        Assert.True(defaultPathStart >= 0 && loadStart > defaultPathStart);
        Assert.DoesNotContain("Directory.CreateDirectory", settingsModel[defaultPathStart..loadStart]);
        Assert.True(
            cli.IndexOf("MachineTransactionLease.Enter", StringComparison.Ordinal) <
            cli.IndexOf("var settings = LoadSettings", StringComparison.Ordinal));
        Assert.Contains("readOnlySettings.AutoFillMissing();", cli);
        Assert.DoesNotContain("readOnlySettings.Save", cli);
        Assert.Contains("MachineTransactionLease.RequireCurrent", settingsModel);
        Assert.Contains("Settings.LoadForMutation(overridePath)", cli);
        Assert.Contains("Settings.LoadForMutation(configPath)", guiAction);
        Assert.Contains("Settings.LoadForMutation(path)",
            File.ReadAllText(Path.Combine(root, "Services", "GuiSettingsTransaction.cs")));
        Assert.Contains("new SettingsWindow(Settings, _transaction)", firstRun);
        Assert.Contains("_settings = welcome.Settings", main);
        var preflightDialog = main.IndexOf("var dlg = new FirstRunWindow(_settings)", StringComparison.Ordinal);
        var preflightHandoff = main.IndexOf("_settings = dlg.Settings", preflightDialog, StringComparison.Ordinal);
        var preflightRefresh = main.IndexOf("UpdateVmbInfoBar()", preflightHandoff, StringComparison.Ordinal);
        Assert.True(preflightDialog >= 0 && preflightHandoff > preflightDialog &&
            preflightRefresh > preflightHandoff,
            "Preflight must adopt the FirstRunWindow settings before refreshing GUI state.");
        Assert.Contains("RequireCurrent(\"ProcessRunner process creation\")", processRunner);
        Assert.Contains("RequireCurrent(\"Publication receipt process creation\")", receiptGate);
        Assert.Contains("RequireCurrent(\"Upload staging\")", uploadStager);
        Assert.Contains("RequireCurrent(\"Upload ACL capture\")", uploadLease);
        Assert.Contains("RequireCurrent(\"Upload ACL recovery\")", uploadLease);
        Assert.Contains("RequireCurrent(\"Mod scaffolding\")", scaffolder);
        Assert.Contains("RequireCurrent(\"itemV2.cfg write\")", discovery);
        Assert.Contains("RequireCurrent(\"itemV2.cfg title synchronization\")", titleSync);
        Assert.Contains("RequireCurrent(\"VMB download/install\")", downloader);
    }

    [Fact]
    public void EveryProductionFilesystemProcessJobAndAclMutationSiteIsExplicitlyReviewed()
    {
        var sources = LoadProductionSources();
        var allowedDiagnostics = new HashSet<string>(StringComparer.Ordinal)
        {
        };
        var actual = ProductionMutationAnalyzer.AnalyzeProduction(
            sources,
            LoadGeneratedSemanticSupportSources(),
            allowedDiagnostics);
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            // Exact semantic call-site inventory. Line/column/occurrence movement
            // is deliberate review friction and requires re-proving ownership.
            // TbLog is generated from XAML, so source-only compilation cannot
            // bind TextBox.AppendText; retain it visibly instead of suppressing it.
            "MainWindow.xaml.cs:152:9:1:UNRESOLVED mutation candidate",
            "Services/MachineTransactionLease.cs:385:9:1:Directory API",
            "Services/MachineTransactionLease.cs:390:33:1:new FileStream",
            "Services/MachineTransactionLease.cs:394:17:1:stream.Write",
            "Services/MachineTransactionLease.cs:395:17:1:stream.Flush",
            "Services/MachineTransactionLease.cs:397:13:1:File API",
            "Services/MachineTransactionLease.cs:399:53:1:File API",
            "Services/MachineTransactionLease.cs:413:9:1:File API",
            "Services/ModDiscovery.cs:130:9:1:File API",
            "Services/ModRunner.cs:105:13:1:File API",
            "Services/ModRunner.cs:103:13:1:File API",
            "Services/ModRunner.cs:112:13:1:File API",
            "Services/ModScaffolder.cs:38:13:1:Directory API",
            "Services/ModScaffolder.cs:45:49:1:Directory API",
            "Services/ModScaffolder.cs:63:9:1:Directory API",
            "Services/ModScaffolder.cs:69:13:1:Directory API",
            "Services/ModScaffolder.cs:75:17:1:File API",
            "Services/ModScaffolder.cs:79:17:1:File API",
            "Services/ModScaffolder.cs:115:9:1:File API",
            "Services/ProcessRunner.cs:51:14:1:Process.instance.Start",
            "Services/ProcessRunner.cs:73:23:1:stream.Write",
            "Services/ProcessRunner.cs:79:72:1:Process.instance.Kill",
            "Services/ProcessTreeGuard.cs:29:26:1:CreateJobObjectW",
            "Services/ProcessTreeGuard.cs:49:26:1:SetInformationJobObject",
            "Services/ProcessTreeGuard.cs:55:22:1:AssignProcessToJobObject",
            "Services/ProcessTreeGuard.cs:126:38:1:Process.instance.Kill",
            "Services/ProcessTreeGuard.cs:180:14:1:CreateProcessW",
            "Services/ProcessTreeGuard.cs:254:19:1:CreateJobObjectW",
            "Services/ProcessTreeGuard.cs:260:18:1:AssignProcessToJobObject",
            "Services/PublicationReceiptGate.cs:904:29:1:Process.Start",
            "Services/PublicationReceiptGate.cs:906:26:1:stream.CopyTo",
            // Receipt authority reconstructs source checkout bytes through the
            // committed blobs and a constrained in-memory EOL transform; it
            // adds no filesystem mutation site.
            "Services/ReceiptAuthorityCommitProof.cs:730:38:1:stream.Write",
            "Services/ReceiptAuthorityCommitProof.cs:731:13:1:stream.Write",
            "Services/Settings.cs:95:9:1:Directory API",
            "Services/Settings.cs:104:33:1:new FileStream",
            "Services/Settings.cs:107:33:1:new StreamWriter",
            "Services/Settings.cs:109:17:1:stream.Write",
            "Services/Settings.cs:110:17:1:stream.Flush",
            "Services/Settings.cs:111:17:1:stream.Flush",
            "Services/Settings.cs:113:13:1:File API",
            "Services/Settings.cs:117:47:1:File API",
            "Services/TitleVersionSync.cs:150:13:1:File API",
            "Services/UploadPathLease.cs:313:9:1:new FileStream",
            "Services/UploadPathLease.cs:388:13:1:FileSystemAclExtensions.SetAccessControl",
            "Services/UploadPathLease.cs:397:22:1:SetKernelObjectSecurity",
            "Services/UploadPathLease.cs:547:13:1:File API",
            "Services/UploadPathLease.cs:679:17:1:File API",
            "Services/UploadPathLease.cs:684:36:1:File API",
            "Services/UploadPathLease.cs:771:13:1:FileSystemAclExtensions.SetAccessControl",
            "Services/UploadPathLease.cs:784:37:1:new FileStream",
            "Services/UploadPathLease.cs:788:21:1:stream.Write",
            "Services/UploadPathLease.cs:789:21:1:stream.Flush",
            "Services/UploadPathLease.cs:791:17:1:File API",
            "Services/UploadPathLease.cs:795:51:1:File API",
            "Services/UploadStager.cs:67:19:1:Directory API",
            "Services/UploadStager.cs:77:9:1:Directory API",
            "Services/UploadStager.cs:84:13:1:File API",
            "Services/UploadStager.cs:99:13:1:File API",
            "Services/UploadStager.cs:192:9:1:File API",
            "Services/UploadStager.cs:223:39:1:new FileStream",
            "Services/UploadStager.cs:254:32:1:new FileStream",
            "Services/UploadStager.cs:287:17:1:stream.SetLength",
            "Services/UploadStager.cs:288:17:1:stream.Write",
            "Services/UploadStager.cs:289:17:1:stream.Flush",
            "Services/UploadStager.cs:298:17:1:stream.SetLength",
            "Services/UploadStager.cs:299:17:1:stream.Write",
            "Services/UploadStager.cs:300:17:1:stream.Flush",
            "Services/UploadStager.cs:365:9:1:stream.CopyTo",
            "Services/VmbDownloader.cs:83:17:1:Directory API",
            "Services/VmbDownloader.cs:85:17:1:ZipFile.ExtractToDirectory",
            "Services/VmbDownloader.cs:95:49:1:File API",
            "Services/VmbDownloader.cs:116:41:1:File API",
            "Services/VmbDownloader.cs:117:51:1:Directory API",
            "Services/VmbDownloader.cs:134:25:1:File API",
            "Services/VmbDownloader.cs:141:19:1:stream.Write",
        };
        Assert.Equal(75, expected.Count);

        Assert.True(expected.SetEquals(actual),
            "Production filesystem/process/job/ACL census drifted.\n" +
            "Unreviewed:\n" + string.Join("\n", actual.Except(expected).Order()) + "\n" +
            "Missing/moved:\n" + string.Join("\n", expected.Except(actual).Order()));

        var shiftedSettings = "// planted line movement\n" + sources["Services/Settings.cs"];
        var shiftedSites = ProductionMutationAnalyzer.Analyze(new Dictionary<string, string>
        {
            ["Services/Settings.cs"] = shiftedSettings,
        });
        Assert.Contains(shiftedSites, site => !expected.Contains(site));
    }

    [Fact]
    public void SemanticCensusClosesPreviouslyMissedReceiverAndLanguageForms()
    {
        var sites = ProductionMutationAnalyzer.Analyze(new Dictionary<string, string>
        {
            ["Services/PlantedSemanticForms.cs"] = """"
                using System;
                using System.IO;
                using IOFile = System.IO.File;
                using Writer = System.IO.StreamWriter;
                using static System.IO.Directory;

                sealed class Holder
                {
                    public FileInfo Info { get; } = new("member");
                }

                sealed class PlantedSemanticForms
                {
                    static FileInfo Factory() => new("factory");

                    void Run(object value, Holder holder, TextWriter textWriter)
                    {
                        IOFile.Delete("alias");
                        Delete("static-import");
                        FileStream targetTyped = new("target", FileMode.Create);
                        using var aliasedWriter = new Writer("alias-constructor");
                        Factory().Replace("destination", "backup");
                        ((FileSystemInfo)value).Delete();
                        holder.Info.Open(FileMode.OpenOrCreate, FileAccess.Write);
                        new DirectoryInfo("immediate").CreateSubdirectory("child");
                        holder.Info.Attributes |= FileAttributes.Hidden;
                        holder.Info.IsReadOnly = true;
                        holder.Info.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                        holder.Info.Attributes++;
                        --holder.Info.UnixFileMode;
                        textWriter.Write("abstract-writer");
                        textWriter.Flush();
                        var interpolated = $"{new FileInfo("interpolated").CopyTo("copy")}";
                        var raw = $"""{new FileInfo("raw").Replace("destination", null)}""";
                        File.CreateSymbolicLink("link", "target");
                        Directory.CreateTempSubdirectory("new-api");
                    }
                }
                """",
        });

        Assert.Equal(2, sites.Count(site => site.EndsWith(":File API", StringComparison.Ordinal)));
        Assert.Equal(2, sites.Count(site => site.EndsWith(":Directory API", StringComparison.Ordinal)));
        Assert.Single(sites, site => site.EndsWith(":new FileStream", StringComparison.Ordinal));
        Assert.Single(sites, site => site.EndsWith(":new StreamWriter", StringComparison.Ordinal));
        Assert.Equal(4, sites.Count(site => site.EndsWith(":FileInfo instance API", StringComparison.Ordinal)));
        Assert.Single(sites, site => site.EndsWith(":FileSystemInfo instance API", StringComparison.Ordinal));
        Assert.Single(sites, site => site.EndsWith(":DirectoryInfo instance API", StringComparison.Ordinal));
        Assert.Equal(5, sites.Count(site =>
            site.EndsWith(":FileSystemInfo property write", StringComparison.Ordinal)));
        Assert.Single(sites, site => site.EndsWith(":stream.Write", StringComparison.Ordinal));
        Assert.Single(sites, site => site.EndsWith(":stream.Flush", StringComparison.Ordinal));
    }

    [Fact]
    public void SemanticCensusFindsMethodGroupMutatorsAndRandomAccessInDebugAndRelease()
    {
        var sites = ProductionMutationAnalyzer.Analyze(new Dictionary<string, string>
        {
            ["Services/PlantedDelegateForms.cs"] = """
                using System;
                using System.IO;
                using Microsoft.Win32.SafeHandles;

                sealed class PlantedDelegateForms
                {
                    void Run(SafeFileHandle handle)
                    {
                #if DEBUG
                        Action<string> delete = File.Delete;
                #else
                        Action<string> delete = File.Delete;
                #endif
                        delete("through-delegate");
                        RandomAccess.Write(handle, new byte[] { 1 }, 0);
                        _ = RandomAccess.WriteAsync(handle, new byte[] { 2 }, 1);
                    }
                }
                """,
        });

        // The preprocessor branches occupy distinct source lines. Seeing both
        // proves the analyzer's debug and release passes each classify the
        // conversion target even though the later Action.Invoke is innocuous.
        Assert.Equal(2, sites.Count(site => site.EndsWith(":File API", StringComparison.Ordinal)));
        Assert.Equal(2, sites.Count(site => site.EndsWith(":RandomAccess.Write", StringComparison.Ordinal)));
        Assert.DoesNotContain(sites, site => site.EndsWith(":UNRESOLVED mutation candidate", StringComparison.Ordinal));
    }

    [Fact]
    public void SemanticCensusPreservesSameLineOccurrencesAndIgnoresReads()
    {
        var duplicates = ProductionMutationAnalyzer.Analyze(new Dictionary<string, string>
        {
            ["Services/PlantedDuplicate.cs"] =
                "using System.IO; class C { void Run() { File.Delete(\"x\"); File.Delete(\"y\"); } }",
        });
        Assert.Equal(2, duplicates.Count);
        Assert.Equal(2, duplicates.Select(site => site.Split(':')[3]).Distinct().Count());

        var reads = ProductionMutationAnalyzer.Analyze(new Dictionary<string, string>
        {
            ["Services/PlantedReads.cs"] = """
                using System.IO;
                class PlantedReads
                {
                    void Run(FileInfo file, DirectoryInfo directory)
                    {
                        _ = File.ReadAllText("x");
                        _ = Directory.EnumerateFiles(".");
                        using var stream = file.OpenRead();
                        _ = directory.GetFiles();
                        _ = file.LastWriteTimeUtc;
                        _ = file.Attributes;
                        _ = file.UnixFileMode;
                        System.Console.Out.Write("console-control");
                    }
                }
                """,
        });
        Assert.Empty(reads);

        var conditional = ProductionMutationAnalyzer.Analyze(new Dictionary<string, string>
        {
            ["Services/PlantedConditional.cs"] = """
                using System.IO;
                class Conditional
                {
                    void Run()
                    {
                #if DEBUG
                        _ = File.ReadAllText("debug-read");
                #else
                        File.Delete("release-mutation");
                #endif
                    }
                }
                """,
        });
        Assert.Single(conditional);
        Assert.EndsWith(":File API", conditional.Single(), StringComparison.Ordinal);

        var unresolved = ProductionMutationAnalyzer.Analyze(new Dictionary<string, string>
        {
            ["Services/PlantedUnresolved.cs"] =
                "class C { void Run(MissingType value) { value.Delete(); } }",
        });
        Assert.Single(unresolved);
        Assert.EndsWith(":UNRESOLVED mutation candidate", unresolved.Single(), StringComparison.Ordinal);

        var strictFailure = Assert.Throws<InvalidOperationException>(() =>
            ProductionMutationAnalyzer.AnalyzeProduction(
                new Dictionary<string, string>
                {
                    ["Services/PlantedUnresolved.cs"] =
                        "class C { void Run(MissingType value) { value.Delete(); } }",
                },
                new Dictionary<string, string>(),
                new HashSet<string>(StringComparer.Ordinal)));
        Assert.Contains("CS0246", strictFailure.Message);
    }

    private static Dictionary<string, string> LoadProductionSources()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(path => (Path: path, Relative: Path.GetRelativePath(root, path).Replace('\\', '/')))
            .Where(item => !item.Relative.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) &&
                           !item.Relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase) &&
                           !item.Relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase) &&
                           !item.Relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) &&
                           !item.Relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(item => item.Relative, item => File.ReadAllText(item.Path), StringComparer.Ordinal);
    }

    private static Dictionary<string, string> LoadGeneratedSemanticSupportSources()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var generatedRoot = Path.Combine(root, "obj", "Debug", "net9.0-windows");
        if (!Directory.Exists(generatedRoot))
            throw new DirectoryNotFoundException(
                $"WPF semantic support was not generated before the census: {generatedRoot}");

        var support = Directory.EnumerateFiles(generatedRoot, "*.g.cs", SearchOption.AllDirectories)
            .ToDictionary(
                path => "__generated/" + Path.GetRelativePath(generatedRoot, path).Replace('\\', '/'),
                File.ReadAllText,
                StringComparer.Ordinal);
        const string tbLogField = "internal System.Windows.Controls.TextBox TbLog;";
        var mainWindowKey = support.Keys.Single(key => key.EndsWith("MainWindow.g.cs", StringComparison.Ordinal));
        if (!support[mainWindowKey].Contains(tbLogField, StringComparison.Ordinal))
            throw new InvalidDataException("Generated MainWindow semantic support no longer contains the exact TbLog field.");

        // Keep all WPF-generated members available to Roslyn, but deliberately
        // type TbLog as dynamic: its AppendText call remains a visible, exact
        // unresolved-sensitive site instead of being mistaken for filesystem IO.
        support[mainWindowKey] = support[mainWindowKey].Replace(
            tbLogField,
            "internal dynamic TbLog;",
            StringComparison.Ordinal);
        return support;
    }
}
