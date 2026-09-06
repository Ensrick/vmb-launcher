using System.IO;

namespace VmbLauncher.Services;

/// <summary>
/// Bounded discovery for rollback/finalization journals. Discovery establishes
/// old transaction ownership only; it never supplies authority for a new
/// forward deployment.
/// </summary>
internal static partial class LocalExactSetDeployment
{
    internal static RunOutcome RecoverInterruptedSafety(
        string? workshopRoot,
        string modName,
        Action<string>? log = null) =>
        RecoverDiscoveredJournals(workshopRoot, modName, recoverAll: false, log);

    internal static RunOutcome RecoverAllInterruptedSafety(
        string? workshopRoot,
        Action<string>? log = null) =>
        RecoverDiscoveredJournals(workshopRoot, requestedMod: null, recoverAll: true, log);

    private static RunOutcome RecoverDiscoveredJournals(
        string? workshopRoot,
        string? requestedMod,
        bool recoverAll,
        Action<string>? log)
    {
        MachineTransactionLease.RequireCurrent("Receipt-authority local recovery");
        try
        {
            if (string.IsNullOrWhiteSpace(workshopRoot) || !Directory.Exists(workshopRoot))
                return new(true, "No interrupted receipt-authority local deployment exists.");

            // A receipt transaction can never own a legacy noncanonical mod
            // name. Return before resolving the Workshop root so unrelated
            // reserved artifacts cannot change uppercase/junction behavior.
            if (!recoverAll &&
                !System.Text.RegularExpressions.Regex.IsMatch(
                    requestedMod ?? "",
                    "^[a-z0-9_]+$"))
                return new(true, "No interrupted receipt-authority local deployment exists.");

            // A configured legacy junction is outside receipt authority. With
            // no canonical root identity to bind it, unrelated reserved bytes
            // must be preserved and ignored rather than changing legacy deploy.
            if (!recoverAll &&
                (File.GetAttributes(workshopRoot) & FileAttributes.ReparsePoint) != 0)
                return new(true, "No interrupted receipt-authority local deployment exists.");

            var parent = Normalize(workshopRoot);
            var journals = DiscoverDurableJournals(parent, strictCorruption: true);
            if (!recoverAll)
            {
                journals = journals
                    .Where(item => string.Equals(
                        item.Journal.Mod,
                        requestedMod,
                        StringComparison.Ordinal))
                    .ToList();
            }
            if (journals.Count == 0)
                return new(true, "No interrupted receipt-authority local deployment exists.");
            if (!recoverAll && journals.Count > 1)
                throw new InvalidDataException(
                    "Multiple interrupted receipt-deploy journals claim the same mod; refusing ambiguous recovery.");

            // Unsupported pre-release journals are read-only evidence. Refuse
            // the whole discovered set before canonical-name restoration or
            // any other recovery mutation can occur.
            foreach (var item in journals)
                RequireSupportedRecoverySchema(item.Journal);

            // Root identity and reserved-artifact constraints apply only after
            // a durable journal has proved an exact recovery owner.
            _ = ImmutableBundleSourceLease.InspectDirectory(parent);
            RefuseJournalTemps(parent);
            var recoveredMods = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in journals.OrderBy(
                         value => value.Journal.PublishedId,
                         StringComparer.Ordinal))
            {
                using var journalLease = RestoreCanonicalJournalNameAndOpenLease(
                    item.Locator,
                    item.Journal,
                    item.SourcePath);
                if (!recoveredMods.Add(item.Journal.Mod))
                    throw new InvalidDataException(
                        "Multiple interrupted receipt-deploy journals claim the same mod; refusing ambiguous recovery.");
                var recovery = ValidateRecoveryJournal(
                    item.Locator,
                    item.Journal,
                    item.Journal.Mod,
                    authorization: null);
                RecoverValidatedJournal(recovery, journalLease, log);
                RefuseUnjournaledArtifacts(item.Locator);
            }
            return new(true, "Recovered interrupted receipt-authority local deployment state safely.");
        }
        catch (Exception ex) when (BypassesAutomaticRecovery(ex))
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(false, $"Receipt-authority local recovery refused: {ex.Message}");
        }
    }

    private static List<(TransactionLocator Locator, DeployJournal Journal, string SourcePath)>
        DiscoverDurableJournals(string parent, bool strictCorruption)
    {
        var result = new List<(
            TransactionLocator Locator,
            DeployJournal Journal,
            string SourcePath)>();
        var scanned = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(
                     parent,
                     ".vmblauncher-receipt-deploy-*.journal.json*",
                     SearchOption.TopDirectoryOnly))
        {
            if (++scanned > MaximumWorkshopJournals)
                throw new InvalidDataException(
                    "Workshop receipt-deploy journal inventory exceeds its safety bound.");
            var name = Path.GetFileName(path);
            var match = System.Text.RegularExpressions.Regex.Match(
                name,
                "^\\.vmblauncher-receipt-deploy-([1-9][0-9]{0,19})\\.journal\\.json(?:\\.retiring-[0-9a-f]{32})?$");
            if (!match.Success) continue;
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new InvalidDataException(
                        $"Workshop receipt-deploy journal is not a regular file: {name}");
                var journal = ReadJournal(path);
                var id = match.Groups[1].Value;
                var locator = TransactionLocator.Create(Path.Combine(parent, id), id);
                _ = locator.FromJournal(journal);
                var canonical = string.Equals(
                    Normalize(path),
                    locator.Journal,
                    StringComparison.OrdinalIgnoreCase);
                if (!canonical && !string.Equals(
                        Normalize(path),
                        Normalize(journal.RetirementJournalPath),
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "Receipt-deploy retirement witness name differs from its durable journal state.");
                result.Add((locator, journal, Normalize(path)));
            }
            catch when (!strictCorruption)
            {
                // Without exact durable ownership, a per-mod legacy recovery
                // probe must preserve and ignore this unrelated object.
            }
        }
        foreach (var group in result.GroupBy(
                     item => item.Locator.Journal,
                     StringComparer.OrdinalIgnoreCase))
            if (group.Count() > 1)
                throw new InvalidDataException(
                    "Receipt-deploy has both canonical and retirement-witness journals; preserving the ambiguous namespace.");
        return result;
    }

    private static ExactJournalLease RestoreCanonicalJournalNameAndOpenLease(
        TransactionLocator locator,
        DeployJournal journal,
        string sourcePath)
    {
        if (string.Equals(sourcePath, locator.Journal, StringComparison.OrdinalIgnoreCase))
            return ExactJournalLease.Open(
                locator.Journal,
                journal,
                "canonical recovery journal lease");
        if (journal.State is not ("cleanup" or "rollback_cleanup"))
            throw new InvalidDataException(
                "Receipt-deploy retirement witness is not in a finalizable durable state.");
        if (File.Exists(locator.Journal) || Directory.Exists(locator.Journal))
            throw new InvalidDataException(
                "Receipt-deploy canonical journal path is occupied beside its retirement witness.");
        FileStream? witness = null;
        RestoredMembershipSealPair? restoredMembershipSeals = null;
        try
        {
            witness = OpenPinnedJournalForUpdate(sourcePath);
            RequireSameJournalVersion(
                ReadJournal(witness, sourcePath),
                journal,
                "retirement-witness recovery authority");
            var paths = locator.FromJournal(journal);
            restoredMembershipSeals = RestoreRecordedMembershipSeals(
                journal,
                paths,
                journal.ParentIdentity.ToPhysical(paths.Parent),
                journal.StageIdentity?.ToPhysical(paths.Target));
            using var parentLease = ExactDirectoryLease.OpenExisting(
                locator.Parent,
                journal.ParentIdentity.ToPhysical(locator.Parent),
                "retirement-witness parent after membership-seal restoration");
            RenamePinnedObject(
                witness.SafeFileHandle,
                locator.Journal,
                replaceIfExists: false,
                parentLease.Handle);
            Checkpoint("journal-retirement-witness-restored");
            var lease = ExactJournalLease.Adopt(
                locator.Journal,
                witness,
                journal,
                "restored canonical recovery journal lease");
            witness = null;
            return lease;
        }
        finally
        {
            restoredMembershipSeals?.Dispose();
            witness?.Dispose();
        }
    }
}
