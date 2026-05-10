using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using VmbLauncher.Services;

namespace VmbLauncher.Views;

public partial class NewModWindow : Window
{
    private readonly Settings _settings;
    private readonly Action<string> _log;
    private static readonly Regex NameRegex = new(@"^[a-z][a-z0-9_]{2,63}$", RegexOptions.Compiled);

    public NewModWindow(Settings settings, Action<string> log)
    {
        _settings = settings;
        _log = log;
        InitializeComponent();
        Loaded += (_, _) => RunPreflight();
    }

    private async void RunPreflight()
    {
        TbPreflight.Text = "Checking environment...";
        await Task.Yield();
        var lines = new List<string>();
        var ok = true;

        var vmb = VmbLocator.Resolve(_settings.VmbRoot);
        if (vmb == null) { lines.Add("✗ VMB not found. Open Settings and pick the VMB folder."); ok = false; }
        else lines.Add($"✓ VMB ({vmb.Flavor}) at {vmb.Root}");

        if (string.IsNullOrEmpty(_settings.UgcToolPath) || !File.Exists(_settings.UgcToolPath))
        { lines.Add("✗ ugc_tool.exe not found. Install \"Vermintide 2 SDK\" via Steam (Library → Tools)."); ok = false; }
        else lines.Add("✓ ugc_tool.exe found");

        if (!SteamLocator.IsSteamRunning())
        { lines.Add("✗ Steam isn't running. Start Steam, then click Create."); ok = false; }
        else lines.Add("✓ Steam is running");

        if (string.IsNullOrEmpty(_settings.WorkshopContentRoot))
        { lines.Add("✗ Workshop content folder for VT2 (552500) not found."); ok = false; }
        else lines.Add($"✓ Workshop folder: {_settings.WorkshopContentRoot}");

        TbPreflight.Text = string.Join("\n", lines);
        UpdateCreateEnabled(ok);
    }

    private bool _preflightPassed;
    private void UpdateCreateEnabled(bool preflightOk)
    {
        _preflightPassed = preflightOk;
        BtnCreate.IsEnabled = preflightOk && IsNameValid();
    }

    private bool IsNameValid() => NameRegex.IsMatch(TbName.Text.Trim());

    private void TbName_TextChanged(object sender, TextChangedEventArgs e)
    {
        BtnCreate.IsEnabled = _preflightPassed && IsNameValid();
        if (string.IsNullOrEmpty(TbTitle.Text) && IsNameValid())
            TbTitle.Text = ToTitleCase(TbName.Text.Trim());
    }

    private static string ToTitleCase(string s)
    {
        var parts = s.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts.Select(p => char.ToUpper(p[0]) + p.Substring(1)));
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void BtnCreate_Click(object sender, RoutedEventArgs e)
    {
        var vmb = VmbLocator.Resolve(_settings.VmbRoot);
        if (vmb == null) { MessageBox.Show(this, "VMB not configured.", "VMB Launcher"); return; }
        var name = TbName.Text.Trim();
        var title = string.IsNullOrWhiteSpace(TbTitle.Text) ? name : TbTitle.Text.Trim();
        var desc = TbDesc.Text;
        var visibility = (CbVisibility.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "private";

        if (visibility == "public")
        {
            var r = MessageBox.Show(this,
                "Are you absolutely sure you want to create this mod with PUBLIC visibility?\n\nPublic mods that get reported are removed irreversibly. Recommend: start as private.",
                "Confirm public mod", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
        }

        var modDir = Path.Combine(vmb.ModsDir, name);
        if (Directory.Exists(modDir))
        {
            MessageBox.Show(this, $"A folder named \"{name}\" already exists in {vmb.ModsDir}. Pick a different name.", "VMB Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BtnCreate.IsEnabled = false;
        BtnCancel.IsEnabled = false;
        TbBusy.Text = "Creating mod (this can take 10-30 seconds)...";

        try
        {
            // Build the vmb create command.
            var args = new List<string>();
            if (vmb.Flavor == VmbFlavor.NodeScript) args.Add(vmb.Executable);
            args.AddRange(new[] { "create", name, "-t", title, "-v", visibility });
            if (!string.IsNullOrEmpty(desc))
            {
                args.Add("-d");
                args.Add(desc);
            }

            var fileName = vmb.Flavor == VmbFlavor.Binary ? vmb.Executable : (vmb.NodePath ?? "node.exe");

            _log($"[create] {name} (visibility={visibility})");
            var result = await ProcessRunner.RunAsync(fileName, args, vmb.Root, _log);

            if (result.ExitCode != 0)
            {
                _log($"[create] FAILED with exit code {result.ExitCode}");

                // VMB's create.js deletes the scaffold on upload failure. Rebuild it manually so the user
                // doesn't lose their work and can fix Steam/SDK issues without losing edits.
                if (!Directory.Exists(modDir))
                {
                    _log("[create] VMB deleted the scaffold after a failed Workshop registration. Rebuilding it locally...");
                    ScaffoldOffline(vmb, name, title, desc, visibility, modDir);
                    MessageBox.Show(this,
                        "VMB couldn't register the mod with Steam Workshop, so it deleted the scaffold.\n\n" +
                        "I rebuilt your mod folder locally with itemV2.cfg ready, but it has no Workshop ID yet — you'll need to upload manually once Steam is happy.\n\n" +
                        "Most common causes:\n" +
                        "  • Steam wasn't running\n" +
                        "  • You don't own VT2 on this account\n" +
                        "  • ugc_tool.exe was missing\n\n" +
                        "Fix that, then click Upload on this mod from the main window.",
                        "Workshop registration failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    MessageBox.Show(this, $"VMB create failed with exit code {result.ExitCode}. Check the log.", "VMB Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                _log($"[create] OK -- mod scaffolded at {modDir}");

                // Try to extract published_id from itemV2.cfg.
                var cfgPath = Path.Combine(modDir, "itemV2.cfg");
                string? id = null;
                if (File.Exists(cfgPath))
                {
                    id = ModDiscovery.ExtractPublishedId(File.ReadAllText(cfgPath));
                }

                if (!string.IsNullOrEmpty(id))
                {
                    _log($"[create] Workshop ID: {id}");
                    var r = MessageBox.Show(this,
                        $"Mod created and registered on Steam Workshop (id {id}).\n\n" +
                        "Steam doesn't auto-subscribe to your own uploads. Click Yes to open the Workshop page now and subscribe (required so Steam creates the local folder we deploy into).",
                        "Subscribe to your mod",
                        MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (r == MessageBoxResult.Yes)
                    {
                        try
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"steam://url/CommunityFilePage/{id}") { UseShellExecute = true });
                        }
                        catch (Exception ex) { _log($"[create] couldn't open Steam URL: {ex.Message}"); }
                    }
                }
            }

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            _log($"[create] EXCEPTION: {ex.Message}");
            MessageBox.Show(this, ex.Message, "VMB Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnCreate.IsEnabled = true;
            BtnCancel.IsEnabled = true;
            TbBusy.Text = "";
        }
    }

    private static void ScaffoldOffline(VmbInstall vmb, string name, string title, string desc, string visibility, string modDir)
    {
        Directory.CreateDirectory(modDir);
        Directory.CreateDirectory(Path.Combine(modDir, "scripts", "mods", name));
        Directory.CreateDirectory(Path.Combine(modDir, "resource_packages", name));

        var cfg = new System.Text.StringBuilder();
        cfg.AppendLine($"title = \"{Esc(title)}\";");
        cfg.AppendLine($"description = \"{Esc(desc)}\";");
        cfg.AppendLine("preview = \"item_preview.png\";");
        cfg.AppendLine("content = \"bundleV2\";");
        cfg.AppendLine("language = \"english\";");
        cfg.AppendLine($"visibility = \"{visibility}\";");
        cfg.AppendLine("apply_for_sanctioned_status = false;");
        cfg.AppendLine("tags = [ ];");
        File.WriteAllText(Path.Combine(modDir, "itemV2.cfg"), cfg.ToString());

        var modFile = $"return new_mod(\"{name}\", {{\n    mod_script       = \"scripts/mods/{name}/{name}\",\n    mod_data         = \"scripts/mods/{name}/{name}_data\",\n    mod_localization = \"scripts/mods/{name}/{name}_localization\",\n}})\n";
        File.WriteAllText(Path.Combine(modDir, $"{name}.mod"), modFile);

        File.WriteAllText(Path.Combine(modDir, "scripts", "mods", name, $"{name}.lua"),
            $"local mod = get_mod(\"{name}\")\n\nmod:info(\"{name} loaded\")\n");
        File.WriteAllText(Path.Combine(modDir, "scripts", "mods", name, $"{name}_data.lua"),
            $"local mod = get_mod(\"{name}\")\nreturn {{\n    name = \"{Esc(title)}\",\n    description = mod:localize(\"{name}_description\"),\n    is_togglable = true,\n}}\n");
        File.WriteAllText(Path.Combine(modDir, "scripts", "mods", name, $"{name}_localization.lua"),
            $"return {{\n    {name}_description = {{ en = \"{Esc(title)}\" }},\n}}\n");
        File.WriteAllText(Path.Combine(modDir, "resource_packages", name, $"{name}.package"),
            $"resources = [\n    \"scripts/mods/{name}/{name}\"\n    \"scripts/mods/{name}/{name}_data\"\n    \"scripts/mods/{name}/{name}_localization\"\n]\n");
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}
