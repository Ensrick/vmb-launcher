using System.Globalization;
using System.IO;

namespace VmbLauncher.Services;

/// <summary>
/// Machine-global ship/version claim gate (monorepo issue #724).
///
/// The monorepo's <c>tools/ship/claim.ps1</c> broker atomically allocates a mod's next
/// MOD_VERSION and records it in a per-checkout <c>.ship_claims/&lt;mod&gt;.claim</c> file that
/// <c>ship.ps1</c> gates on. That gate only exists in checkouts whose <c>ship.ps1</c> postdates
/// PR 757 — a parallel session shipping from an older worktree bypasses it entirely, and because
/// each worktree has its own <c>.ship_claims/</c>, a per-checkout gate can never see another
/// checkout's claims (field incident 2026-07-18: ct_dev collided twice, 0.7.295-dev and
/// 0.7.296-dev, the 20:00 upload clobbering the 19:17 fix build).
///
/// The fix: claim.ps1 mirrors every claim into a MACHINE-GLOBAL directory,
/// <c>%APPDATA%\VMBLauncher\ship_claims\</c> (same base dir as settings.json), and the launcher —
/// the single chokepoint every upload passes through regardless of checkout vintage — evaluates
/// that mirror before staging anything for ugc_tool:
/// <list type="bullet">
/// <item>live claim (&lt; 2 h) whose version != the source MOD_VERSION → REFUSE (exit 3): two
///   sessions are racing different versions at the same Workshop item;</item>
/// <item>live claim that matches → one OK line, proceed;</item>
/// <item>no claim / stale claim / unreadable claim → WARN and proceed. A missing claim must not
///   brick old-workflow ships; the gate exists to stop MISMATCHED live claims.</item>
/// </list>
/// <c>--no-claim</c> skips the check with a loud warning (parity with ship.ps1 -NoClaim).
/// </summary>
public static class ShipClaimGate
{
    /// <summary>Claims older than this are stale — mirrors claim.ps1's -StaleHours default.</summary>
    public const double StaleHours = 2.0;

    /// <summary>
    /// The machine-global claims directory: <c>%APPDATA%\VMBLauncher\ship_claims\</c>. Same base
    /// dir as settings.json (<see cref="Settings.DefaultConfigPath"/>) but deliberately NOT via
    /// that helper — this must not create the directory as a side effect of evaluating a gate.
    /// </summary>
    public static string DefaultClaimsDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VMBLauncher", "ship_claims");

    /// <summary>Path of the claim file for one mod inside <paramref name="claimsDir"/>.</summary>
    public static string ClaimPath(string claimsDir, string modName) =>
        Path.Combine(claimsDir, modName + ".claim");

    public enum Verdict
    {
        /// <summary>No claim file exists for this mod — warn (unclaimed upload) and proceed.</summary>
        NoClaim,
        /// <summary>A claim exists but is ≥ 2 h old — treated like no claim (warn and proceed).</summary>
        Stale,
        /// <summary>A live claim exists and its version equals the source MOD_VERSION — proceed.</summary>
        Match,
        /// <summary>A live claim exists for a DIFFERENT version — refuse the upload (exit 3).</summary>
        Mismatch,
        /// <summary>A claim file exists but could not be read/parsed — warn and proceed.</summary>
        Unreadable,
    }

    /// <summary>Parsed body of a claim file (claim.ps1 Format-ClaimContent).</summary>
    public sealed record ClaimInfo(string Mod, string Version, string Session, DateTime CreatedUtc);

    public sealed record Evaluation(Verdict Verdict, ClaimInfo? Claim, string? Detail)
    {
        /// <summary>Claim age in hours at evaluation time; null when no parseable claim.</summary>
        public double? AgeHours { get; init; }
    }

    /// <summary>
    /// Parse a claim file body. Format (see tools/ship/CLAIMS.md):
    /// <code>
    /// # VT2 ship/version claim -- see tools/ship/CLAIMS.md
    /// mod = chaos_wastes_tweaker_dev
    /// version = 0.7.297-dev
    /// session = &lt;id&gt;
    /// created = 2026-07-18T04:12:33Z
    /// </code>
    /// Returns null when any required key (mod/version/session/created) is missing or the
    /// timestamp is unparseable — mirroring claim.ps1's Read-ClaimFile, which treats such a
    /// file as "not a valid live claim".
    /// </summary>
    public static ClaimInfo? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            var eq = t.IndexOf('=');
            if (eq < 1) continue;
            map[t[..eq].Trim()] = t[(eq + 1)..].Trim();
        }
        if (!map.TryGetValue("mod", out var mod) ||
            !map.TryGetValue("version", out var version) ||
            !map.TryGetValue("session", out var session) ||
            !map.TryGetValue("created", out var createdText))
            return null;

        var styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        if (!DateTimeOffset.TryParse(createdText, CultureInfo.InvariantCulture, styles, out var created))
            return null;

        return new ClaimInfo(mod, version, session, created.UtcDateTime);
    }

    /// <summary>
    /// Evaluate the machine-global claim state for <paramref name="modName"/> against the
    /// MOD_VERSION about to ship. Pure with respect to the filesystem apart from one read;
    /// never creates directories or files.
    /// </summary>
    public static Evaluation Evaluate(string claimsDir, string modName, string sourceVersion, DateTime nowUtc, double staleHours = StaleHours)
    {
        var path = ClaimPath(claimsDir, modName);
        if (!File.Exists(path))
            return new Evaluation(Verdict.NoClaim, null, path);

        string raw;
        try { raw = File.ReadAllText(path); }
        catch (Exception ex) { return new Evaluation(Verdict.Unreadable, null, $"{path}: {ex.Message}"); }

        var claim = Parse(raw);
        if (claim == null)
            return new Evaluation(Verdict.Unreadable, null, $"{path}: not a parseable claim (missing keys or bad timestamp)");

        var age = (nowUtc - claim.CreatedUtc).TotalHours;
        if (age >= staleHours)
            return new Evaluation(Verdict.Stale, claim, path) { AgeHours = age };

        return string.Equals(claim.Version, sourceVersion, StringComparison.Ordinal)
            ? new Evaluation(Verdict.Match, claim, path) { AgeHours = age }
            : new Evaluation(Verdict.Mismatch, claim, path) { AgeHours = age };
    }
}
