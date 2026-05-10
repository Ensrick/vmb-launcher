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
    // Looser than VMB convention but matches what Windows allows for a folder name.
    // Name must start with a letter and contain only letters/digits/underscore. 2-64 chars.
    private static readonly Regex NameRegex = new(@"^[A-Za-z][A-Za-z0-9_]{1,63}$", RegexOptions.Compiled);

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
        RefreshButton();
    }

    private bool IsNameValid() => NameRegex.IsMatch(TbName.Text.Trim());

    /// <summary>Per-keystroke validation. Returns the error to display, or null if the name is valid.</summary>
    public static string? ValidateName(string raw)
    {
        var name = (raw ?? "").Trim();
        if (string.IsNullOrEmpty(name)) return "Type a name to continue.";
        if (name.Length < 2) return "Name must be at least 2 characters.";
        if (name.Length > 64) return "Name must be 64 characters or fewer.";
        if (!char.IsLetter(name[0])) return "Name must start with a letter.";
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
                return $"Name can only contain letters, digits, and underscores. \"{c}\" isn't allowed.";
        }
        return null;
    }

    private void RefreshButton()
    {
        var nameError = ValidateName(TbName.Text);
        TbNameHint.Text = nameError ?? "";
        BtnCreate.IsEnabled = _preflightPassed && nameError == null;
        BtnCreate.ToolTip = !_preflightPassed
            ? "Pre-flight has errors — fix the red ✗ items above."
            : nameError ?? "Click to create the mod and register it on Workshop.";
    }

    private void TbName_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshButton();
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

        var project = VmbProject.Resolve(_settings.ProjectRoot) ?? VmbProject.Resolve(_settings.VmbRoot);
        if (project == null) { MessageBox.Show(this, "Project folder not configured.", "VMB Launcher"); return; }

        var modDir = Path.Combine(project.ModsDir, name);
        if (Directory.Exists(modDir))
        {
            MessageBox.Show(this, $"A folder named \"{name}\" already exists in {project.ModsDir}. Pick a different name.", "VMB Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BtnCreate.IsEnabled = false;
        BtnCancel.IsEnabled = false;
        TbBusy.Text = "Scaffolding mod...";

        try
        {
            // We deliberately do NOT shell out to `vmb create`. VMB v1.8.4's create.js calls the
            // uploader immediately on a freshly-scaffolded mod with an empty bundleV2/, ugc_tool
            // refuses with "empty content directory", and VMB then deletes the scaffold. See
            // ModScaffolder.cs for full reasoning. The user registers on Workshop later via
            // Build → Upload — ugc_tool creates the Workshop entry on first upload when the
            // itemV2.cfg has no published_id.
            _log($"[create] scaffolding {name} (visibility={visibility})");
            var req = new ModScaffoldRequest(name, title, desc, visibility);
            var result = await Task.Run(() => ModScaffolder.Scaffold(vmb, project, req));

            if (!result.Ok)
            {
                _log($"[create] FAILED: {result.Message}");
                MessageBox.Show(this, result.Message, "Scaffold failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _log($"[create] OK -- {modDir}");
            MessageBox.Show(this,
                $"Scaffolded {name} at:\n{modDir}\n\n" +
                "To register the mod on Steam Workshop, edit your scripts, then click " +
                "Build → Upload from the main window. Steam Workshop will create a new " +
                "entry on the first successful upload and write its ID back into itemV2.cfg.",
                "Mod scaffolded", MessageBoxButton.OK, MessageBoxImage.Information);

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

}
