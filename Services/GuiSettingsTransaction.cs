namespace VmbLauncher.Services;

/// <summary>
/// Holds the machine mutation transaction for the complete lifetime of a GUI
/// setup/settings dialog. The scope intentionally permits any project root,
/// because changing ProjectRoot is one of the dialog's jobs.
/// </summary>
internal sealed class GuiSettingsTransaction : IDisposable
{
    private readonly MachineTransactionLease _lease;

    private GuiSettingsTransaction(MachineTransactionLease lease) => _lease = lease;

    internal static GuiSettingsTransaction Enter(
        string action,
        Action<string>? log = null,
        TimeSpan? timeout = null,
        string? recordPath = null,
        string? mutexName = null) =>
        new(MachineTransactionLease.Enter(
            action, mod: null, projectRoot: null, log, timeout, recordPath, mutexName));

    internal GuiSettingsTransaction Borrow(string action)
    {
        MachineTransactionLease.RequireCurrent("GUI settings nested handoff");
        return new GuiSettingsTransaction(MachineTransactionLease.Enter(
            action, mod: null, projectRoot: null));
    }

    internal Settings Reload(string path)
    {
        MachineTransactionLease.RequireCurrent("GUI settings reload");
        return Settings.LoadForMutation(path);
    }

    internal void Save(Settings settings)
    {
        MachineTransactionLease.RequireCurrent("GUI settings save");
        settings.Save();
    }
    public void Dispose() => _lease.Dispose();
}
