namespace VmbLauncher.Services;

/// <summary>
/// Audited shell-only escape from transaction process-tree containment.
/// Targets opened here must not mutate VMB, project, staging, release, or
/// Workshop state. All tool processes belong in <see cref="ProcessRunner"/>.
/// </summary>
internal static class NonMutationShell
{
    internal static void Open(string target)
    {
        using var process = ProcessTreeGuard.StartBreakawayExplorer(new[] { target });
    }

    internal static void SelectFile(string path)
    {
        using var process = ProcessTreeGuard.StartBreakawayExplorer(new[] { "/select," + path });
    }
}
