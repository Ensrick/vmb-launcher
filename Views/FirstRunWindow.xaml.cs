using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using VmbLauncher.Services;

namespace VmbLauncher.Views;

public partial class FirstRunWindow : Window
{
    public Settings Settings { get; }

    public FirstRunWindow(Settings s)
    {
        Settings = s;
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        // Always re-run auto-detection in case the user installed something between checks.
        if (Settings.AutoFillMissing()) Settings.Save();

        SpItems.Children.Clear();
        var checks = Diagnostics.RunAll(Settings);
        foreach (var c in checks) SpItems.Children.Add(BuildRow(c));

        BtnContinue.IsEnabled = !Diagnostics.HasErrors(checks);
        BtnContinue.Content = Diagnostics.HasIssues(checks) ? "Continue with warnings" : "Looks good — let's go";
    }

    private UIElement BuildRow(CheckResult c)
    {
        var bg = c.Status switch
        {
            CheckStatus.Ok => "#0F2A14",
            CheckStatus.Warn => "#2A2410",
            _ => "#2A1414"
        };
        var icon = c.Status switch
        {
            CheckStatus.Ok => "✓",
            CheckStatus.Warn => "!",
            _ => "✗"
        };
        var iconColor = c.Status switch
        {
            CheckStatus.Ok => "#4EC9B0",
            CheckStatus.Warn => "#DCDCAA",
            _ => "#F48771"
        };

        var border = new Border
        {
            Background = (Brush)new BrushConverter().ConvertFromString(bg)!,
            BorderBrush = (Brush)new BrushConverter().ConvertFromString("#3E3E42")!,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10, 14, 12),
            Margin = new Thickness(0, 0, 0, 8),
            CornerRadius = new CornerRadius(3),
        };

        var stack = new StackPanel();
        var header = new DockPanel { LastChildFill = true };
        var iconTb = new TextBlock { Text = icon, FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 10, 0), Foreground = (Brush)new BrushConverter().ConvertFromString(iconColor)! };
        var titleTb = new TextBlock { Text = c.Title, FontSize = 15, FontWeight = FontWeights.SemiBold };
        DockPanel.SetDock(iconTb, Dock.Left);
        header.Children.Add(iconTb);
        header.Children.Add(titleTb);

        var detailTb = new TextBlock { Text = c.Detail, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(28, 4, 0, 0), Foreground = (Brush)new BrushConverter().ConvertFromString("#CCCCCC")! };

        stack.Children.Add(header);
        stack.Children.Add(detailTb);

        if (c.FixAction != null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(28, 8, 0, 0) };
            foreach (var (label, action) in BuildFixButtons(c.FixAction))
            {
                var b = new Button { Content = label };
                b.Click += (_, _) => { action(); Refresh(); };
                sp.Children.Add(b);
            }
            stack.Children.Add(sp);
        }

        border.Child = stack;
        return border;
    }

    private IEnumerable<(string Label, Action Action)> BuildFixButtons(string fixAction)
    {
        switch (fixAction)
        {
            case "browse-vmb":
                yield return ("Auto-detect", () => { var v = VmbLocator.AutoDetect(); if (v != null) Settings.VmbRoot = v.Root; Settings.Save(); });
                yield return ("Browse...", () => { var dlg = new OpenFolderDialog { Title = "Pick VMB folder" }; if (dlg.ShowDialog(this) == true) { Settings.VmbRoot = dlg.FolderName; Settings.Save(); } });
                yield return ("Open VMB github", () => OpenUrl("https://github.com/Vermintide-Mod-Framework/Vermintide-Mod-Builder/releases"));
                break;
            case "browse-project":
                yield return ("Auto-detect", () => { var p = VmbProject.AutoDetect(Settings.VmbRoot); if (p != null) Settings.ProjectRoot = p.Root; Settings.Save(); });
                yield return ("Browse...", () => { var dlg = new OpenFolderDialog { Title = "Pick the folder where your mods live (contains .vmbrc)" }; if (dlg.ShowDialog(this) == true) { Settings.ProjectRoot = dlg.FolderName; Settings.Save(); } });
                break;
            case "configure-project":
                yield return ("Configure now", async () =>
                {
                    var vmb = VmbLocator.Resolve(Settings.VmbRoot);
                    if (vmb == null) return;
                    var projectRoot = Settings.ProjectRoot ?? Settings.VmbRoot ?? Environment.CurrentDirectory;
                    // If project folder is the same as VMB folder, default mods_dir=mods. Otherwise use "." (project-root layout).
                    var modsDirArg = string.Equals(projectRoot, Settings.VmbRoot, StringComparison.OrdinalIgnoreCase) ? "mods" : ".";
                    var args = new List<string>();
                    if (vmb.Flavor == VmbFlavor.NodeScript) args.Add(vmb.Executable);
                    args.Add("config"); args.Add($"--mods_dir={modsDirArg}"); args.Add("--cwd");
                    var fileName = vmb.Flavor == VmbFlavor.Binary ? vmb.Executable : (vmb.NodePath ?? "node.exe");
                    try { await ProcessRunner.RunAsync(fileName, args, projectRoot, _ => { }); } catch { }
                });
                break;
            case "browse-steam":
                yield return ("Auto-detect", () => { var p = SteamLocator.FindSteamInstall(); if (p != null) Settings.SteamRoot = p; Settings.Save(); });
                yield return ("Browse...", () => { var dlg = new OpenFolderDialog { Title = "Pick Steam folder" }; if (dlg.ShowDialog(this) == true) { Settings.SteamRoot = dlg.FolderName; Settings.Save(); } });
                break;
            case "start-steam":
                yield return ("Start Steam", () => { try { Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true }); } catch { } });
                break;
            case "install-sdk":
                yield return ("Open SDK in Steam", () => OpenUrl("steam://run/718610"));
                yield return ("Auto-detect", () => { var p = SteamLocator.FindVt2Sdk(); if (p != null) Settings.Vt2SdkRoot = p; Settings.Save(); });
                yield return ("Browse...", () => { var dlg = new OpenFolderDialog { Title = "Pick Vermintide 2 SDK folder" }; if (dlg.ShowDialog(this) == true) { Settings.Vt2SdkRoot = dlg.FolderName; Settings.Save(); } });
                break;
            case "browse-tool":
                yield return ("Auto-detect", () => { var p = SteamLocator.FindUgcTool(); if (p != null) Settings.UgcToolPath = p; Settings.Save(); });
                yield return ("Browse...", () => { var dlg = new OpenFileDialog { Title = "Pick ugc_tool.exe", Filter = "ugc_tool.exe|ugc_tool.exe" }; if (dlg.ShowDialog(this) == true) { Settings.UgcToolPath = dlg.FileName; Settings.Save(); } });
                break;
            case "open-workshop":
                yield return ("Auto-detect", () => { var p = SteamLocator.FindWorkshopContentRoot(); if (p != null) Settings.WorkshopContentRoot = p; Settings.Save(); });
                yield return ("Browse VT2 Workshop", () => OpenUrl("https://steamcommunity.com/app/552500/workshop/"));
                break;
        }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void BtnRecheck_Click(object sender, RoutedEventArgs e) => Refresh();

    private void BtnOpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(Settings) { Owner = this };
        if (dlg.ShowDialog() == true) Refresh();
    }

    private void BtnContinue_Click(object sender, RoutedEventArgs e)
    {
        Settings.ConfirmedFirstRun = true;
        Settings.Save();
        DialogResult = true;
        Close();
    }

    private void BtnSkip_Click(object sender, RoutedEventArgs e)
    {
        Settings.ConfirmedFirstRun = true;
        Settings.Save();
        DialogResult = false;
        Close();
    }
}
