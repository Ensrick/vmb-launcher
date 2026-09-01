using System.IO;

namespace VmbLauncher.Services;

/// <summary>
/// Validates and restores the durable parent/target NTFS membership-seal pair
/// before recovery opens any delete-capable directory lease.
/// </summary>
internal static partial class LocalExactSetDeployment
{
    private sealed class RestoredMembershipSealPair : IDisposable
    {
        private LocalExactSetMembershipSeal? _parent;
        private LocalExactSetMembershipSeal? _target;

        internal RestoredMembershipSealPair(
            LocalExactSetMembershipSeal parent,
            LocalExactSetMembershipSeal target)
        {
            _parent = parent;
            _target = target;
        }

        internal void RequireOriginal()
        {
            _target!.RequireOriginal();
            _parent!.RequireOriginal();
        }

        public void Dispose()
        {
            var target = _target;
            _target = null;
            var parent = _parent;
            _parent = null;
            try
            {
                target?.Dispose();
            }
            finally
            {
                parent?.Dispose();
            }
        }
    }

    private static void ValidateMembershipSealPlans(
        DeployJournal journal,
        TransactionPaths paths,
        PhysicalDirectoryIdentity parentIdentity,
        PhysicalDirectoryIdentity? targetIdentity)
    {
        var hasParent = journal.ParentMembershipSeal != null;
        var hasTarget = journal.TargetMembershipSeal != null;
        if (hasParent != hasTarget)
            throw new InvalidDataException(
                "Receipt-deploy journal has an incomplete membership-seal plan pair.");
        if (!hasParent)
        {
            if (journal.State == "cleanup")
                throw new InvalidDataException(
                    "Committed receipt-deploy journal lacks its membership-seal authority.");
            return;
        }
        if (journal.State is not ("verified" or "cleanup" or "rollback_cleanup" or "manual_review"))
            throw new InvalidDataException(
                "Receipt-deploy journal carries membership-seal plans before the verified boundary.");
        if (targetIdentity == null)
            throw new InvalidDataException(
                "Receipt-deploy membership-seal plan lacks its replacement identity.");

        var parentPlan = journal.ParentMembershipSeal!.ToPlan();
        var targetPlan = journal.TargetMembershipSeal!.ToPlan();
        if (parentPlan.DeniedRights !=
                checked((int)LocalExactSetMembershipSeal.NamespaceDeniedRights) ||
            targetPlan.DeniedRights !=
                checked((int)LocalExactSetMembershipSeal.NamespaceDeniedRights) ||
            !parentPlan.ToIdentity().SameObject(parentIdentity) ||
            !targetPlan.ToIdentity().SameObject(targetIdentity) ||
            !string.Equals(
                Normalize(parentPlan.Path),
                paths.Parent,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Normalize(targetPlan.Path),
                paths.Target,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Receipt-deploy membership-seal plans do not match the exact journal identities and paths.");
    }

    private static RestoredMembershipSealPair? RestoreRecordedMembershipSeals(
        DeployJournal journal,
        TransactionPaths paths,
        PhysicalDirectoryIdentity parentIdentity,
        PhysicalDirectoryIdentity? targetIdentity)
    {
        ValidateMembershipSealPlans(journal, paths, parentIdentity, targetIdentity);
        if (journal.ParentMembershipSeal == null) return null;

        LocalExactSetMembershipSeal? parent = null;
        LocalExactSetMembershipSeal? target = null;
        try
        {
            parent = LocalExactSetMembershipSeal.Resume(
                journal.ParentMembershipSeal.ToPlan(),
                parentIdentity);
            target = LocalExactSetMembershipSeal.Resume(
                journal.TargetMembershipSeal!.ToPlan(),
                targetIdentity!);
            target.Restore();
            parent.Restore();
            target.RequireOriginal();
            parent.RequireOriginal();
            Checkpoint("membership-seals-restored-recovery");
            var restored = new RestoredMembershipSealPair(parent, target);
            parent = null;
            target = null;
            return restored;
        }
        catch
        {
            target?.AbandonWithoutRestore();
            parent?.AbandonWithoutRestore();
            target = null;
            parent = null;
            throw;
        }
        finally
        {
            target?.Dispose();
            parent?.Dispose();
        }
    }
}
