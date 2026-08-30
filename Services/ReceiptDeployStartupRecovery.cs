namespace VmbLauncher.Services;

/// <summary>
/// Production ordering seam for receipt-deploy availability recovery. Callers
/// must invoke it immediately after a strict settings load and before any
/// autofill, persistence, diagnostics, project/mod discovery, or source access.
/// </summary>
internal static class ReceiptDeployStartupRecovery
{
    internal static RunOutcome RunBeforeDiscovery(
        Settings settings,
        string? requestedMod,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        MachineTransactionLease.RequireCurrent("Receipt-deploy startup recovery");
        var outcome = requestedMod == null
            ? LocalExactSetDeployment.RecoverAllInterruptedSafety(
                settings.WorkshopContentRoot,
                log)
            : LocalExactSetDeployment.RecoverInterruptedSafety(
                settings.WorkshopContentRoot,
                requestedMod,
                log);
        return outcome;
    }
}
