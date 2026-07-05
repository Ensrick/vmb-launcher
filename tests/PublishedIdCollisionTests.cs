using VmbLauncher.Services;

namespace VmbLauncher.Tests;

/// <summary>
/// Guards the 2026-06-19 incident: gui_tweaker's itemV2.cfg carried
/// general_tweaker_dev's published_id (3733367409), so every `upload gui_tweaker`
/// hijacked gt_dev's Workshop item. The upload/all commands now abort on a shared
/// published_id; this pins the detection logic behind that gate.
/// </summary>
public class PublishedIdCollisionTests
{
    private static ModInfo Mk(string name, string publishedId) => new ModInfo
    {
        Name = name,
        ModDir = $"C:/fake/{name}",
        ItemCfgPath = $"C:/fake/{name}/itemV2.cfg",
        PublishedId = publishedId,
    };

    [Fact]
    public void Detects_collision_between_two_mods_sharing_an_id()
    {
        // The exact incident shape: gut carrying gt_dev's id.
        var gtDev = Mk("general_tweaker_dev", "3733367409");
        var gut   = Mk("gui_tweaker",        "3733367409");
        var all   = new List<ModInfo> { gtDev, gut, Mk("weapon_tweaker", "3712896117") };

        var hit = ModDiscovery.FindPublishedIdCollision(gut, all);

        Assert.NotNull(hit);
        Assert.Equal("general_tweaker_dev", hit!.Name);
    }

    [Fact]
    public void Returns_null_when_id_is_unique()
    {
        var wt  = Mk("weapon_tweaker", "3712896117");
        var all = new List<ModInfo> { wt, Mk("general_tweaker", "3713619122") };

        Assert.Null(ModDiscovery.FindPublishedIdCollision(wt, all));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("")]
    public void Ignores_unpublished_sentinel_ids(string id)
    {
        // Two new, never-uploaded mods both at the "0"/empty sentinel are NOT a collision.
        var a = Mk("new_mod_a", id);
        var b = Mk("new_mod_b", id);

        Assert.Null(ModDiscovery.FindPublishedIdCollision(a, new List<ModInfo> { a, b }));
    }

    [Fact]
    public void Does_not_flag_a_mod_against_itself()
    {
        var wt = Mk("weapon_tweaker", "3712896117");

        Assert.Null(ModDiscovery.FindPublishedIdCollision(wt, new List<ModInfo> { wt }));
    }
}
