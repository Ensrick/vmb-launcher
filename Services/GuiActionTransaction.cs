namespace VmbLauncher.Services;

/// <summary>
/// Owns the machine-wide mutation boundary for one GUI action and replaces the
/// window's possibly stale settings snapshot only after the lease is held.
/// </summary>
internal sealed class GuiActionTransaction : IDisposable
{
    private readonly MachineTransactionLease _lease;

    internal Settings Settings { get; }
    internal VmbProject Project { get; }

    private GuiActionTransaction(
        MachineTransactionLease lease, Settings settings, VmbProject project)
    {
        _lease = lease;
        Settings = settings;
        Project = project;
    }

    internal static GuiActionTransaction Enter(
        Settings displayedSettings,
        string action,
        string? mod,
        Action<string>? log = null,
        TimeSpan? timeout = null,
        string? recordPath = null,
        string? mutexName = null)
    {
        ArgumentNullException.ThrowIfNull(displayedSettings);
        var configPath = displayedSettings.ConfigPath;
        var lease = MachineTransactionLease.Enter(
            action, mod, projectRoot: null, log, timeout, recordPath, mutexName);
        try
        {
            // ConfigPath identifies the shared settings document; every value
            // inside it is untrusted until reloaded while serialized.
            var current = Settings.LoadForMutation(configPath);
            if (current.AutoFillMissing()) current.Save();
            var project = current.ResolveMutationProject()
                ?? throw new InvalidOperationException("Project folder not configured.");
            // Bind the root that downstream VmbProject fallback resolution will
            // actually mutate, never merely the raw preferred setting.
            MachineTransactionLease.RefineOwnedProjectRoot(project.Root);
            return new GuiActionTransaction(lease, current, project);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public void Dispose() => _lease.Dispose();
}
