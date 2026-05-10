using System.IO;
using VmbLauncher.Services;

namespace VmbLauncher.Tests;

public class ModScaffolderTests
{
    private sealed class FakeVmb : IDisposable
    {
        public TempDir VmbDir { get; }
        public TempDir ProjectDir { get; }
        public FakeVmb(bool withTemplate)
        {
            VmbDir = new TempDir();
            File.WriteAllBytes(Path.Combine(VmbDir.Path, "vmb.exe"), Array.Empty<byte>());
            if (withTemplate)
            {
                var t = VmbDir.CreateSubdir(".template-vmf");
                File.WriteAllText(Path.Combine(t, "%%name.mod"), "name=%%name title=%%title");
                File.WriteAllBytes(Path.Combine(t, "item_preview.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
                var pkgDir = Path.Combine(t, "resource_packages", "%%name");
                Directory.CreateDirectory(pkgDir);
                File.WriteAllText(Path.Combine(pkgDir, "%%name.package"), "package=%%name");
                var luaDir = Path.Combine(t, "scripts", "mods", "%%name");
                Directory.CreateDirectory(luaDir);
                File.WriteAllText(Path.Combine(luaDir, "%%name.lua"), "-- get_mod(\"%%name\")");
            }
            ProjectDir = new TempDir();
            File.WriteAllText(Path.Combine(ProjectDir.Path, ".vmbrc"), "{ \"mods_dir\": \".\" }");
        }
        public void Dispose() { VmbDir.Dispose(); ProjectDir.Dispose(); }
    }

    [Fact]
    public void FindTemplateDir_prefers_template_vmf_over_template()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(Path.Combine(t.Path, ".template-vmf"));
        Directory.CreateDirectory(Path.Combine(t.Path, ".template"));
        Assert.EndsWith(".template-vmf", ModScaffolder.FindTemplateDir(t.Path));
    }

    [Fact]
    public void FindTemplateDir_falls_back_to_template_when_no_template_vmf()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(Path.Combine(t.Path, ".template"));
        Assert.EndsWith(".template", ModScaffolder.FindTemplateDir(t.Path));
    }

    [Fact]
    public void FindTemplateDir_returns_null_when_neither_exists()
    {
        using var t = new TempDir();
        Assert.Null(ModScaffolder.FindTemplateDir(t.Path));
    }

    [Fact]
    public void Substitute_replaces_name_in_paths_and_text()
    {
        var req = new ModScaffoldRequest("MyMod", "Title", "Desc", "private");
        Assert.Equal("MyMod.mod", ModScaffolder.Substitute("%%name.mod", req));
        Assert.Equal("scripts/mods/MyMod/MyMod.lua", ModScaffolder.Substitute("scripts/mods/%%name/%%name.lua", req));
        Assert.Equal("name=MyMod title=Title", ModScaffolder.Substitute("name=%%name title=%%title", req));
    }

    [Fact]
    public void Substitute_replaces_description()
    {
        var req = new ModScaffoldRequest("X", "T", "Hello world", "private");
        Assert.Equal("Hello world", ModScaffolder.Substitute("%%description", req));
    }

    [Fact]
    public void EscapeForLuaString_escapes_quotes_and_backslashes()
    {
        Assert.Equal("a\\\"b", ModScaffolder.EscapeForLuaString("a\"b"));
        Assert.Equal("a\\\\b", ModScaffolder.EscapeForLuaString("a\\b"));
        Assert.Equal("a\\nb", ModScaffolder.EscapeForLuaString("a\nb"));
    }

    [Fact]
    public void IsTextFile_treats_known_text_extensions_as_text()
    {
        Assert.True(ModScaffolder.IsTextFile("a.mod"));
        Assert.True(ModScaffolder.IsTextFile("a.package"));
        Assert.True(ModScaffolder.IsTextFile("a.lua"));
        Assert.True(ModScaffolder.IsTextFile("a.cfg"));
        Assert.True(ModScaffolder.IsTextFile("a.MOD")); // case insensitive
        Assert.True(ModScaffolder.IsTextFile("noext"));
    }

    [Fact]
    public void IsTextFile_treats_binary_as_binary()
    {
        Assert.False(ModScaffolder.IsTextFile("preview.png"));
        Assert.False(ModScaffolder.IsTextFile("a.dds"));
        Assert.False(ModScaffolder.IsTextFile("a.jpg"));
        Assert.False(ModScaffolder.IsTextFile("a.zip"));
    }

    [Fact]
    public void Scaffold_creates_mod_folder_with_template_files()
    {
        using var fake = new FakeVmb(true);
        var v = VmbLocator.Resolve(fake.VmbDir.Path)!;
        var p = VmbProject.Resolve(fake.ProjectDir.Path)!;
        var project = fake.ProjectDir;

        var result = ModScaffolder.Scaffold(v, p, new ModScaffoldRequest("MyMod", "My Mod", "desc", "private"));

        Assert.True(result.Ok, result.Message);
        var modDir = Path.Combine(project.Path, "MyMod");
        Assert.True(Directory.Exists(modDir));
        Assert.True(File.Exists(Path.Combine(modDir, "MyMod.mod")));
        Assert.True(File.Exists(Path.Combine(modDir, "item_preview.png")));
        Assert.True(File.Exists(Path.Combine(modDir, "resource_packages", "MyMod", "MyMod.package")));
        Assert.True(File.Exists(Path.Combine(modDir, "scripts", "mods", "MyMod", "MyMod.lua")));
        Assert.True(File.Exists(Path.Combine(modDir, "itemV2.cfg")));

    }

    [Fact]
    public void Scaffold_substitutes_placeholders_in_text_files()
    {
        using var fake = new FakeVmb(true);
        var v = VmbLocator.Resolve(fake.VmbDir.Path)!;
        var p = VmbProject.Resolve(fake.ProjectDir.Path)!;
        var project = fake.ProjectDir;

        ModScaffolder.Scaffold(v, p, new ModScaffoldRequest("MyMod", "My Mod", "desc", "private"));

        var modContent = File.ReadAllText(Path.Combine(project.Path, "MyMod", "MyMod.mod"));
        Assert.Contains("name=MyMod", modContent);
        Assert.Contains("title=My Mod", modContent);

        var lua = File.ReadAllText(Path.Combine(project.Path, "MyMod", "scripts", "mods", "MyMod", "MyMod.lua"));
        Assert.Contains("get_mod(\"MyMod\")", lua);

    }

    [Fact]
    public void Scaffold_does_not_substitute_in_binary_files()
    {
        using var fake = new FakeVmb(true);
        var v = VmbLocator.Resolve(fake.VmbDir.Path)!;
        var p = VmbProject.Resolve(fake.ProjectDir.Path)!;
        var project = fake.ProjectDir;

        ModScaffolder.Scaffold(v, p, new ModScaffoldRequest("MyMod", "T", "D", "private"));

        var bytes = File.ReadAllBytes(Path.Combine(project.Path, "MyMod", "item_preview.png"));
        // PNG magic: 0x89 0x50 0x4E 0x47
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes);

    }

    [Fact]
    public void Scaffold_writes_itemcfg_with_no_published_id()
    {
        using var fake = new FakeVmb(true);
        var v = VmbLocator.Resolve(fake.VmbDir.Path)!;
        var p = VmbProject.Resolve(fake.ProjectDir.Path)!;
        var project = fake.ProjectDir;

        ModScaffolder.Scaffold(v, p, new ModScaffoldRequest("MyMod", "My Title", "Some desc", "private"));

        var cfg = File.ReadAllText(Path.Combine(project.Path, "MyMod", "itemV2.cfg"));
        Assert.Contains("title = \"My Title\";", cfg);
        Assert.Contains("description = \"Some desc\";", cfg);
        Assert.Contains("visibility = \"private\";", cfg);
        Assert.Contains("content = \"bundleV2\";", cfg);
        Assert.DoesNotContain("published_id", cfg);

    }

    [Fact]
    public void Scaffold_fails_when_folder_already_exists()
    {
        using var fake = new FakeVmb(true);
        var v = VmbLocator.Resolve(fake.VmbDir.Path)!;
        var p = VmbProject.Resolve(fake.ProjectDir.Path)!;
        var project = fake.ProjectDir;
        Directory.CreateDirectory(Path.Combine(project.Path, "Existing"));

        var result = ModScaffolder.Scaffold(v, p, new ModScaffoldRequest("Existing", "T", "D", "private"));

        Assert.False(result.Ok);
        Assert.Contains("already exists", result.Message);

    }

    [Fact]
    public void Scaffold_fails_when_no_template_in_vmb_folder()
    {
        using var fake = new FakeVmb(false);
        var v = VmbLocator.Resolve(fake.VmbDir.Path)!;
        var p = VmbProject.Resolve(fake.ProjectDir.Path)!;

        var result = ModScaffolder.Scaffold(v, p, new ModScaffoldRequest("MyMod", "T", "D", "private"));

        Assert.False(result.Ok);
        Assert.Contains("template", result.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Scaffold_escapes_quotes_in_title_in_itemcfg()
    {
        using var fake = new FakeVmb(true);
        var v = VmbLocator.Resolve(fake.VmbDir.Path)!;
        var p = VmbProject.Resolve(fake.ProjectDir.Path)!;
        var project = fake.ProjectDir;

        ModScaffolder.Scaffold(v, p, new ModScaffoldRequest("MyMod", "My \"quoted\" title", "Line 1\nLine 2", "private"));

        var cfg = File.ReadAllText(Path.Combine(project.Path, "MyMod", "itemV2.cfg"));
        Assert.Contains("title = \"My \\\"quoted\\\" title\";", cfg);
        Assert.Contains("description = \"Line 1\\nLine 2\";", cfg);

    }
}
