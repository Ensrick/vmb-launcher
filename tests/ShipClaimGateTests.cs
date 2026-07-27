using System.IO;
using VmbLauncher.Services;

namespace VmbLauncher.Tests;

/// <summary>
/// Pins for the machine-global ship/version claim gate (monorepo issue #724): the launcher-side
/// evaluation of %APPDATA%\VMBLauncher\ship_claims\&lt;mod&gt;.claim mirrors written by the
/// monorepo's tools/ship/claim.ps1. Publication is fail-closed unless mod,
/// version, owner, and the 24-hour live window all match exactly.
/// </summary>
public class ShipClaimGateTests
{
    private static readonly DateTime Now = new(2026, 7, 18, 20, 0, 0, DateTimeKind.Utc);

    private static string ClaimBody(string mod, string version, string session, DateTime createdUtc) =>
        "# VT2 ship/version claim -- see tools/ship/CLAIMS.md\n" +
        $"mod = {mod}\n" +
        $"version = {version}\n" +
        $"session = {session}\n" +
        $"created = {createdUtc:yyyy-MM-dd'T'HH:mm:ss'Z'}\n";

    // ---- Parse -------------------------------------------------------------------------------

    [Fact]
    public void Parse_ReadsAllFourKeys()
    {
        var claim = ShipClaimGate.Parse(ClaimBody("ct_dev", "0.7.297-dev", "sess-a", Now.AddMinutes(-10)));
        Assert.NotNull(claim);
        Assert.Equal("ct_dev", claim!.Mod);
        Assert.Equal("0.7.297-dev", claim.Version);
        Assert.Equal("sess-a", claim.Session);
        Assert.Equal(Now.AddMinutes(-10), claim.CreatedUtc);
    }

    [Fact]
    public void Parse_ToleratesCrLfAndCommentAndBlankLines()
    {
        var body = "# comment\r\n\r\nmod = m\r\nversion = 1.0.0\r\nsession = s\r\ncreated = 2026-07-18T19:50:00Z\r\n";
        var claim = ShipClaimGate.Parse(body);
        Assert.NotNull(claim);
        Assert.Equal("1.0.0", claim!.Version);
        Assert.Equal(DateTimeKind.Utc, claim.CreatedUtc.Kind);
    }

    [Fact]
    public void Parse_NullOnMissingRequiredKey()
    {
        var noCreated = "mod = m\nversion = 1.0.0\nsession = s\n";
        Assert.Null(ShipClaimGate.Parse(noCreated));
    }

    [Fact]
    public void Parse_NullOnBadTimestamp()
    {
        var bad = "mod = m\nversion = 1.0.0\nsession = s\ncreated = not-a-time\n";
        Assert.Null(ShipClaimGate.Parse(bad));
    }

    [Fact]
    public void Parse_NullOnEmptyOrWhitespace()
    {
        Assert.Null(ShipClaimGate.Parse(""));
        Assert.Null(ShipClaimGate.Parse("   \n  "));
    }

    // ---- Evaluate ----------------------------------------------------------------------------

    [Fact]
    public void Evaluate_NoClaim_WhenFileAbsent()
    {
        using var tmp = new TempDir();
        var eval = ShipClaimGate.Evaluate(tmp.Path, "modx", "1.0.0", Now);
        Assert.Equal(ShipClaimGate.Verdict.NoClaim, eval.Verdict);
        Assert.Null(eval.Claim);
    }

    [Fact]
    public void Evaluate_NoClaim_WhenClaimsDirDoesNotExist()
    {
        using var tmp = new TempDir();
        var eval = ShipClaimGate.Evaluate(Path.Combine(tmp.Path, "nonexistent"), "modx", "1.0.0", Now);
        Assert.Equal(ShipClaimGate.Verdict.NoClaim, eval.Verdict);
    }

    [Fact]
    public void Evaluate_Match_WhenLiveClaimVersionEqualsSource()
    {
        using var tmp = new TempDir();
        tmp.Write("modx.claim", ClaimBody("modx", "0.7.297-dev", "sess-a", Now.AddMinutes(-30)));
        var eval = ShipClaimGate.Evaluate(tmp.Path, "modx", "0.7.297-dev", Now);
        Assert.Equal(ShipClaimGate.Verdict.Match, eval.Verdict);
        Assert.Equal("sess-a", eval.Claim!.Session);
    }

    [Fact]
    public void Evaluate_Mismatch_WhenLiveClaimVersionDiffers()
    {
        // The exact issue-724 incident shape: a parallel session holds a live claim for a
        // DIFFERENT next version of the same mod.
        using var tmp = new TempDir();
        tmp.Write("modx.claim", ClaimBody("modx", "0.7.296-dev", "sess-other", Now.AddMinutes(-43)));
        var eval = ShipClaimGate.Evaluate(tmp.Path, "modx", "0.7.295-dev", Now);
        Assert.Equal(ShipClaimGate.Verdict.Mismatch, eval.Verdict);
        Assert.Equal("0.7.296-dev", eval.Claim!.Version);
        Assert.Equal("sess-other", eval.Claim.Session);
        Assert.True(eval.AgeHours > 0.7 && eval.AgeHours < 0.72);
    }

    [Fact]
    public void Evaluate_Stale_WhenClaimIsOlderThanTwentyFourHours()
    {
        using var tmp = new TempDir();
        tmp.Write("modx.claim", ClaimBody("modx", "9.9.9-dev", "sess-old", Now.AddHours(-25)));
        var eval = ShipClaimGate.Evaluate(tmp.Path, "modx", "1.0.0", Now);
        Assert.Equal(ShipClaimGate.Verdict.Stale, eval.Verdict);
    }

    [Fact]
    public void Evaluate_StaleBoundary_ExactlyTwentyFourHoursIsStale()
    {
        // Mirrors claim.ps1 Test-ClaimStale: age >= StaleHours is stale.
        using var tmp = new TempDir();
        tmp.Write("modx.claim", ClaimBody("modx", "9.9.9-dev", "s", Now.AddHours(-ShipClaimGate.StaleHours)));
        var eval = ShipClaimGate.Evaluate(tmp.Path, "modx", "9.9.9-dev", Now);
        Assert.Equal(ShipClaimGate.Verdict.Stale, eval.Verdict);
    }

    [Fact]
    public void Evaluate_Unreadable_WhenFileIsNotAClaim()
    {
        using var tmp = new TempDir();
        tmp.Write("modx.claim", "garbage with no keys");
        var eval = ShipClaimGate.Evaluate(tmp.Path, "modx", "1.0.0", Now);
        Assert.Equal(ShipClaimGate.Verdict.Unreadable, eval.Verdict);
    }

    [Fact]
    public void Evaluate_VersionComparisonIsOrdinal_SuffixMatters()
    {
        // "0.7.297" vs "0.7.297-dev" must NOT match — the suffix is part of the allocation.
        using var tmp = new TempDir();
        tmp.Write("modx.claim", ClaimBody("modx", "0.7.297-dev", "s", Now.AddMinutes(-5)));
        var eval = ShipClaimGate.Evaluate(tmp.Path, "modx", "0.7.297", Now);
        Assert.Equal(ShipClaimGate.Verdict.Mismatch, eval.Verdict);
    }

    [Fact]
    public void Evaluate_OwnerMismatch_WhenVersionMatchesButSessionDiffers()
    {
        using var tmp = new TempDir();
        tmp.Write("modx.claim", ClaimBody("modx", "1.0.0", "owner-a", Now.AddMinutes(-5)));
        var eval = ShipClaimGate.Evaluate(tmp.Path, "modx", "1.0.0", Now, "owner-b");
        Assert.Equal(ShipClaimGate.Verdict.OwnerMismatch, eval.Verdict);
    }

    [Fact]
    public void Evaluate_Match_RequiresExactOwnerWhenProvided()
    {
        using var tmp = new TempDir();
        tmp.Write("modx.claim", ClaimBody("modx", "1.0.0", "owner-a", Now.AddMinutes(-5)));
        var eval = ShipClaimGate.Evaluate(tmp.Path, "modx", "1.0.0", Now, "owner-a");
        Assert.Equal(ShipClaimGate.Verdict.Match, eval.Verdict);
    }

    // ---- Path composition ---------------------------------------------------------------------

    [Fact]
    public void ClaimPath_UsesModFolderNamePlusClaimExtension()
    {
        var p = ShipClaimGate.ClaimPath(@"C:\claims", "chaos_wastes_tweaker_dev");
        Assert.Equal(@"C:\claims\chaos_wastes_tweaker_dev.claim", p);
    }

    [Fact]
    public void DefaultClaimsDir_IsUnderAppDataVmbLauncher()
    {
        var dir = ShipClaimGate.DefaultClaimsDir();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.Equal(Path.Combine(appData, "VMBLauncher", "ship_claims"), dir);
    }

    [Fact]
    public void DefaultClaimsDir_DoesNotCreateTheDirectory()
    {
        // Evaluating the gate must never mkdir under %APPDATA% — only claim.ps1 creates it.
        var dir = ShipClaimGate.DefaultClaimsDir();
        var existedBefore = Directory.Exists(dir);
        _ = ShipClaimGate.Evaluate(dir, "zzz_no_such_mod_zzz", "0.0.0", Now);
        Assert.Equal(existedBefore, Directory.Exists(dir));
    }
}
