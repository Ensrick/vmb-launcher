using System.Globalization;
using System.IO;

namespace VmbLauncher.Services;

/// <summary>
/// Fail-closed reader for the machine-global claim created by
/// tools/ship/claim.ps1. A claim is version allocation and owner coordination;
/// it is necessary for publication but never sufficient without a publication
/// hosted publication receipt.
/// </summary>
public static class ShipClaimGate
{
    public const double StaleHours = 24.0;

    public static string DefaultClaimsDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VMBLauncher", "ship_claims");

    public static string ClaimPath(string claimsDir, string modName) =>
        Path.Combine(claimsDir, modName + ".claim");

    public enum Verdict
    {
        NoClaim,
        Stale,
        Match,
        Mismatch,
        OwnerMismatch,
        Unreadable,
    }

    public sealed record ClaimInfo(string Mod, string Version, string Session, DateTime CreatedUtc);

    public sealed record Evaluation(Verdict Verdict, ClaimInfo? Claim, string? Detail)
    {
        public double? AgeHours { get; init; }
    }

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

    public static Evaluation Evaluate(
        string claimsDir,
        string modName,
        string sourceVersion,
        DateTime nowUtc,
        string? expectedOwner = null,
        double staleHours = StaleHours)
    {
        var path = ClaimPath(claimsDir, modName);
        if (!File.Exists(path))
            return new Evaluation(Verdict.NoClaim, null, path);

        string raw;
        try { raw = File.ReadAllText(path); }
        catch (Exception ex) { return new Evaluation(Verdict.Unreadable, null, $"{path}: {ex.Message}"); }

        var claim = Parse(raw);
        if (claim == null)
            return new Evaluation(Verdict.Unreadable, null, $"{path}: not a parseable claim");

        var age = (nowUtc - claim.CreatedUtc).TotalHours;
        if (age >= staleHours)
            return new Evaluation(Verdict.Stale, claim, path) { AgeHours = age };
        if (!string.Equals(claim.Mod, modName, StringComparison.Ordinal))
            return new Evaluation(Verdict.Mismatch, claim, "claim mod does not match requested mod") { AgeHours = age };
        if (!string.Equals(claim.Version, sourceVersion, StringComparison.Ordinal))
            return new Evaluation(Verdict.Mismatch, claim, "claim version does not match source version") { AgeHours = age };
        if (expectedOwner != null && !string.Equals(claim.Session, expectedOwner, StringComparison.Ordinal))
            return new Evaluation(Verdict.OwnerMismatch, claim, "claim belongs to another owner") { AgeHours = age };
        return new Evaluation(Verdict.Match, claim, path) { AgeHours = age };
    }
}
