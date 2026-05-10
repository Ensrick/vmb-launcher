using System.IO;
using System.Windows;
using Microsoft.Win32;
using VmbLauncher.Services;

namespace VmbLauncher.Views;

public partial class SettingsWindow : Window
{
    public Settings Settings { get; private set; }

    public SettingsWindow(Settings s)
    {
        Settings = s;
        InitializeComponent();
        Load();
        Loaded += (_, _) => RunDiagnostics();
    }

    private void Load()
    {
        TbVmb.Text     = Settings.VmbRoot ?? "";
        TbProject.Text = Settings.ProjectRoot ?? "";
        TbSteam.Text   = Settings.SteamRoot ?? "";
        TbSdk.Text     = Settings.Vt2SdkRoot ?? "";
        TbTool.Text    = Settings.UgcToolPath ?? "";
        TbWs.Text      = Settings.WorkshopContentRoot ?? "";
        TbNode.Text    = Settings.NodePath ?? "";
    }

    private void Stash()
    {
        Settings.VmbRoot              = NullIfEmpty(TbVmb.Text);
        Settings.ProjectRoot          = NullIfEmpty(TbProject.Text);
        Settings.SteamRoot            = NullIfEmpty(TbSteam.Text);
        Settings.Vt2SdkRoot           = NullIfEmpty(TbSdk.Text);
        Settings.UgcToolPath          = NullIfEmpty(TbTool.Text);
        Settings.WorkshopContentRoot  = NullIfEmpty(TbWs.Text);
        Settings.NodePath             = NullIfEmpty(TbNode.Text);
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void BrowseFolder(System.Windows.Controls.TextBox tb)
    {
        var dlg = new OpenFolderDialog { Title = "Pick folder", InitialDirectory = Directory.Exists(tb.Text) ? tb.Text : "" };
        if (dlg.ShowDialog(this) == true) tb.Text = dlg.FolderName;
    }
    private void BrowseFile(System.Windows.Controls.TextBox tb, string filter)
    {
        var dlg = new OpenFileDialog { Filter = filter };
        if (File.Exists(tb.Text)) dlg.InitialDirectory = Path.GetDirectoryName(tb.Text);
        if (dlg.ShowDialog(this) == true) tb.Text = dlg.FileName;
    }

    private void BrowseVmb_Click(object s, RoutedEventArgs e) => BrowseFolder(TbVmb);
    private void BrowseProject_Click(object s, RoutedEventArgs e) => BrowseFolder(TbProject);
    private void BrowseSteam_Click(object s, RoutedEventArgs e) => BrowseFolder(TbSteam);
    private void BrowseSdk_Click(object s, RoutedEventArgs e) => BrowseFolder(TbSdk);
    private void BrowseTool_Click(object s, RoutedEventArgs e) => BrowseFile(TbTool, "ugc_tool.exe|ugc_tool.exe|All exes (*.exe)|*.exe");
    private void BrowseWs_Click(object s, RoutedEventArgs e) => BrowseFolder(TbWs);
    private void BrowseNode_Click(object s, RoutedEventArgs e) => BrowseFile(TbNode, "node.exe|node.exe|All exes (*.exe)|*.exe");

    private void AutoVmb_Click(object s, RoutedEventArgs e)     { var v = VmbLocator.AutoDetect(); if (v != null) TbVmb.Text = v.Root; }
    private void AutoProject_Click(object s, RoutedEventArgs e) { var p = VmbProject.AutoDetect(NullIfEmpty(TbVmb.Text)); if (p != null) TbProject.Text = p.Root; }
    private void AutoSteam_Click(object s, RoutedEventArgs e) { var p = SteamLocator.FindSteamInstall(); if (p != null) TbSteam.Text = p; }
    private void AutoSdk_Click(object s, RoutedEventArgs e)   { var p = SteamLocator.FindVt2Sdk(); if (p != null) TbSdk.Text = p; }
    private void AutoTool_Click(object s, RoutedEventArgs e)  { var p = SteamLocator.FindUgcTool(); if (p != null) TbTool.Text = p; }
    private void AutoWs_Click(object s, RoutedEventArgs e)    { var p = SteamLocator.FindWorkshopContentRoot(); if (p != null) TbWs.Text = p; }
    private void AutoNode_Click(object s, RoutedEventArgs e)  { var p = VmbLocator.FindNode(); if (p != null) TbNode.Text = p; }

    private void RunDiagnostics()
    {
        Stash();
        var lines = new List<string>();
        var vmb = VmbLocator.Resolve(Settings.VmbRoot);
        lines.Add(vmb != null ? $"✓ VMB resolved as {vmb.Flavor} at {vmb.Root}" : "✗ VMB not resolved (no vmb.exe and no vmb.js+node)");
        if (vmb != null && !vmb.HasVmbRc) lines.Add("  ⚠ no .vmbrc found in VMB root — run 'vmb config --mods_dir=mods' once before building");
        if (vmb != null && !Directory.Exists(vmb.ModsDir)) lines.Add($"  ⚠ mods folder doesn't exist: {vmb.ModsDir}");

        lines.Add(string.IsNullOrEmpty(Settings.SteamRoot) || !Directory.Exists(Settings.SteamRoot)
            ? "✗ Steam root not set or missing" : $"✓ Steam at {Settings.SteamRoot}");
        lines.Add(SteamLocator.IsSteamRunning() ? "✓ Steam is running" : "⚠ Steam isn't running (uploads will fail until you start it)");

        lines.Add(string.IsNullOrEmpty(Settings.Vt2SdkRoot) || !Directory.Exists(Settings.Vt2SdkRoot)
            ? "✗ VT2 SDK not found — install via Steam: Library → Tools → Vermintide 2 SDK" : $"✓ VT2 SDK at {Settings.Vt2SdkRoot}");

        lines.Add(string.IsNullOrEmpty(Settings.UgcToolPath) || !File.Exists(Settings.UgcToolPath)
            ? "✗ ugc_tool.exe missing" : $"✓ ugc_tool.exe at {Settings.UgcToolPath}");

        lines.Add(string.IsNullOrEmpty(Settings.WorkshopContentRoot) || !Directory.Exists(Settings.WorkshopContentRoot)
            ? "✗ Workshop content folder for 552500 not found" : $"✓ Workshop folder at {Settings.WorkshopContentRoot}");

        TbDiag.Text = string.Join("\n", lines);
    }

    private void BtnTest_Click(object sender, RoutedEventArgs e) => RunDiagnostics();

    private void BtnOpenSettingsFile_Click(object sender, RoutedEventArgs e)
    {
        Settings.Save();
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{Settings.ConfigPath}\"") { UseShellExecute = true }); }
        catch { }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        Stash();
        Settings.Save();
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
