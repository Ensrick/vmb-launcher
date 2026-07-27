using System.Security.Cryptography;
using System.Text;
using System.IO;

namespace VmbLauncher.Services;

/// <summary>Canonical owner identity shared with tools/ship/claim.ps1.</summary>
public static class ShipOwnerId
{
    public static string Resolve(string repoRoot)
    {
        var explicitId = Environment.GetEnvironmentVariable("VT2_SHIP_SESSION_ID");
        if (!string.IsNullOrWhiteSpace(explicitId)) return $"explicit:{explicitId}";
        var claude = Environment.GetEnvironmentVariable("CLAUDE_SESSION_ID");
        if (!string.IsNullOrWhiteSpace(claude)) return $"claude:{claude}";
        var codex = Environment.GetEnvironmentVariable("CODEX_THREAD_ID");
        if (!string.IsNullOrWhiteSpace(codex)) return $"codex:{codex}";

        if (string.IsNullOrWhiteSpace(repoRoot))
            throw new ArgumentException("A repository root is required to derive the ship owner.", nameof(repoRoot));
        var normalized = Path.GetFullPath(repoRoot).TrimEnd('\\', '/').ToLowerInvariant();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"worktree:{Convert.ToHexString(digest).ToLowerInvariant()[..16]}";
    }
}
