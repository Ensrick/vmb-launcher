using VmbLauncher.Services;
using System.IO;

namespace VmbLauncher.Tests;

public abstract class MutationTestBase : IDisposable
{
    private readonly TempDir _transactionRoot = new();
    private readonly MachineTransactionLease _transaction;

    protected MutationTestBase()
    {
        _transaction = MachineTransactionLease.Enter(
            "xunit-mutation-fixture", mod: null, _transactionRoot.Path,
            recordPath: Path.Combine(_transactionRoot.Path, "owner.json"),
            mutexName: @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        _transaction.Dispose();
        _transactionRoot.Dispose();
    }
}
