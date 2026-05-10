using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using VmbLauncher.Services;
using VmbLauncher.Views;

namespace VmbLauncher;

public sealed class ModRow : INotifyPropertyChanged
{
    public ModInfo Info { get; }
    public string Name => Info.Name;
    public string Title => string.IsNullOrEmpty(Info.Title) ? Info.Name : Info.Title;
    public string Visibility => Info.Visibility;
    public string BuiltStatus => Info.HasBuildOutput ? $"{Info.BundleCount} bundle(s)" : "no build";

    public ModRow(ModInfo info) { Info = info; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Visibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BuiltStatus)));
    }
}

public partial class MainWindow : Window
{
    private Settings _settings;
    private List<ModRow> _mods = new();
    private ModRow? _current;
    private CancellationTokenSource? _runCts;

    public MainWindow()
    {
        InitializeComponent();
        _settings = Settings.Load();
        Loaded += async (_, _) => await InitialiseAsync();
    }

    private async Task InitialiseAsync()
    {
        var changed = _settings.AutoFillMissing();
        if (changed) _settings.Save();

        UpdateVmbInfoBar();

        // Show first-run dialog the first time, OR any time errors exist (until they're addressed
        // and the user confirms by clicking Continue / Skip). Hide MainWindow while it's up so
        // the user only sees one window.
        var checks = Diagnostics.RunAll(_settings);
        var blocked = !_settings.ConfirmedFirstRun || Diagnostics.HasErrors(checks);
        if (blocked)
        {
            Visibility = System.Windows.Visibility.Hidden;
            // FirstRunWindow shown without an Owner so it appears as its own window in the taskbar
            // until MainWindow becomes visible.
            var welcome = new FirstRunWindow(_settings);
            welcome.ShowDialog();
            _settings.Save();
            Visibility = System.Windows.Visibility.Visible;
            Activate();
            UpdateVmbInfoBar();
        }

        if (string.IsNullOrEmpty(_settings.VmbRoot))
        {
            Append("VMB still not configured. Open Settings to point to your VMB folder.");
            SetStatus("VMB not configured");
            return;
        }

        await Task.Run(LoadMods);
    }

    /// <summary>Modal preflight gate. Returns true if the action can proceed.</summary>
    private bool Preflight(string actionLabel, params string[] requiredFor)
    {
        var checks = Diagnostics.RunAll(_settings);
        // Filter to those relevant for this action.
        var blocking = checks.Where(c => c.Status == CheckStatus.Error && requiredFor.Contains(c.Title)).ToList();
        if (blocking.Count == 0) return true;

        var msg = $"Can't {actionLabel.ToLowerInvariant()} yet. Fix:\n\n" + string.Join("\n", blocking.Select(b => $"  • {b.Title} — {b.Detail}"));
        var r = MessageBox.Show(this, msg + "\n\nOpen setup to fix?", $"{actionLabel} blocked", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r == MessageBoxResult.Yes)
        {
            var dlg = new FirstRunWindow(_settings) { Owner = this };
            dlg.ShowDialog();
            _settings.Save();
            UpdateVmbInfoBar();
        }
        return false;
    }

    private void UpdateVmbInfoBar()
    {
        var parts = new List<string>();
        var vmb = VmbLocator.Resolve(_settings.VmbRoot);
        parts.Add(vmb != null ? $"VMB: {vmb.Flavor}" : "VMB: not found");
        parts.Add(SteamLocator.IsSteamRunning() ? "Steam: running" : "Steam: not running");
        if (!string.IsNullOrEmpty(_settings.WorkshopContentRoot)) parts.Add("Workshop: ok");
        else parts.Add("Workshop: missing");
        TbVmbInfo.Text = string.Join("   ·   ", parts);
    }

    private void LoadMods()
    {
        if (string.IsNullOrEmpty(_settings.VmbRoot)) return;
        var found = ModDiscovery.ScanMods(_settings);
        var project = VmbProject.Resolve(_settings.ProjectRoot) ?? VmbProject.Resolve(_settings.VmbRoot);
        var modsDir = project?.ModsDir ?? "(unknown)";
        Dispatcher.Invoke(() =>
        {
            _mods = found.Select(m => new ModRow(m)).ToList();
            LbMods.ItemsSource = _mods;
            SetStatus($"{_mods.Count} mod(s) found in {modsDir}");
            Append($"Loaded {_mods.Count} mod(s) from {modsDir}");
        });
    }

    private void LbMods_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _current = LbMods.SelectedItem as ModRow;
        if (_current == null)
        {
            TbModTitle.Text = "Select a mod";
            TbModSub.Text = "";
            SpActions.IsEnabled = false;
            return;
        }
        var i = _current.Info;
        TbModTitle.Text = string.IsNullOrEmpty(i.Title) ? i.Name : i.Title;
        TbModSub.Text = $"{i.Name}  ·  visibility: {i.Visibility}  ·  workshop id: {(string.IsNullOrEmpty(i.PublishedId) ? "(none)" : i.PublishedId)}  ·  {(i.HasBuildOutput ? $"{i.BundleCount} bundle(s) built" : "not built yet")}";
        SpActions.IsEnabled = true;
        BtnSubscribe.IsEnabled = !string.IsNullOrEmpty(i.PublishedId);
    }

    private void Append(string line)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Append(line)); return; }
        TbLog.AppendText(line + Environment.NewLine);
        TbLog.ScrollToEnd();
    }

    private void SetStatus(string s)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetStatus(s)); return; }
        TbStatusBar.Text = s;
    }

    private async Task RunActionAsync(Func<ModRunner, ModInfo, CancellationToken, Task<RunOutcome>> action, string label)
    {
        if (_current == null) return;
        if (_runCts != null) { Append("Another action is already running."); return; }
        _runCts = new CancellationTokenSource();
        SpActions.IsEnabled = false;
        SetStatus($"{label}: {_current.Info.Name}...");
        try
        {
            var runner = new ModRunner(_settings, Append);
            var outcome = await action(runner, _current.Info, _runCts.Token);
            if (outcome.Ok)
            {
                Append($"[{label.ToLowerInvariant()}] {outcome.Message}");
                SetStatus($"{label} OK");
            }
            else
            {
                Append($"[{label.ToLowerInvariant()}] FAILED: {outcome.Message}");
                SetStatus($"{label} failed");
                MessageBox.Show(this, outcome.Message, $"{label} failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            var fresh = ModDiscovery.ScanMods(_settings).FirstOrDefault(m => m.Name == _current.Info.Name);
            if (fresh != null)
            {
                CopyTo(fresh, _current.Info);
                _current.Refresh();
                LbMods_SelectionChanged(LbMods, null!);
            }
        }
        catch (Exception ex)
        {
            Append($"[{label.ToLowerInvariant()}] EXCEPTION: {ex.Message}");
            SetStatus($"{label} crashed");
        }
        finally
        {
            _runCts?.Dispose();
            _runCts = null;
            SpActions.IsEnabled = true;
            UpdateVmbInfoBar();
        }
    }

    private static void CopyTo(ModInfo src, ModInfo dst)
    {
        dst.Title = src.Title;
        dst.Description = src.Description;
        dst.Visibility = src.Visibility;
        dst.PublishedId = src.PublishedId;
        dst.HasBuildOutput = src.HasBuildOutput;
        dst.BundleCount = src.BundleCount;
    }

    private async void BtnBuild_Click(object sender, RoutedEventArgs e)
    {
        if (!Preflight("Build", "VMB", "Project folder")) return;
        await RunActionAsync((r, m, ct) => r.BuildAsync(m, clean: false, ct), "Build");
    }

    private async void BtnDeploy_Click(object sender, RoutedEventArgs e)
    {
        if (!Preflight("Deploy", "VMB", "Project folder", "Workshop content folder")) return;
        await RunActionAsync((r, m, ct) => r.DeployAsync(m, ct), "Deploy");
    }

    private async void BtnUpload_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        if (!Preflight("Upload", "VMB", "Project folder", "Vermintide 2 SDK", "ugc_tool.exe", "Steam")) return;
        bool allowPublic = false;
        if (_current.Info.IsPublic)
        {
            var r = MessageBox.Show(this,
                "This mod has visibility = \"public\". Public mods can be flagged irreversibly. Continue uploading?",
                "Confirm public upload", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
            allowPublic = true;
        }
        await RunActionAsync((r, m, ct) => r.UploadAsync(m, allowPublic, ct), "Upload");
    }

    private async void BtnAll_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        if (!Preflight("Full pipeline", "VMB", "Project folder", "Vermintide 2 SDK", "ugc_tool.exe", "Steam", "Workshop content folder")) return;
        bool allowPublic = false;
        if (_current.Info.IsPublic)
        {
            var r = MessageBox.Show(this,
                "This mod has visibility = \"public\". Public mods can be flagged irreversibly. Continue with full pipeline?",
                "Confirm public upload", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
            allowPublic = true;
        }
        await RunActionAsync(async (run, m, ct) =>
        {
            var b = await run.BuildAsync(m, clean: false, ct);
            if (!b.Ok) return b;
            var d = await run.DeployAsync(m, ct);
            if (!d.Ok) return d;
            return await run.UploadAsync(m, allowPublic, ct);
        }, "Full pipeline");
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        Append("Refreshing mod list...");
        Task.Run(LoadMods);
        UpdateVmbInfoBar();
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_settings) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _settings = dlg.Settings;
            _settings.Save();
            UpdateVmbInfoBar();
            Task.Run(LoadMods);
        }
    }

    private void BtnNewMod_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_settings.VmbRoot))
        {
            MessageBox.Show(this, "VMB folder not configured. Open Settings first.", "VMB Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var wiz = new NewModWindow(_settings, Append) { Owner = this };
        if (wiz.ShowDialog() == true)
        {
            Task.Run(LoadMods);
        }
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e) => TbLog.Clear();

    private void BtnSubscribe_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || string.IsNullOrEmpty(_current.Info.PublishedId)) return;
        try { Process.Start(new ProcessStartInfo($"steam://url/CommunityFilePage/{_current.Info.PublishedId}") { UseShellExecute = true }); }
        catch (Exception ex) { Append($"Failed to open Steam URL: {ex.Message}"); }
    }

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_current.Info.ModDir}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Append($"Failed to open folder: {ex.Message}"); }
    }
}
