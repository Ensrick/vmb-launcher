using VmbLauncher.Services;

namespace VmbLauncher.Tests;

public class SteamLocatorTests
{
    private const string ClientDll = @"C:\Program Files (x86)\Steam\steamclient.dll";

    [Fact]
    public void Workshop_readiness_rejects_no_live_steam()
    {
        var result = Evaluate(new Dictionary<int, string>(), 123);

        Assert.False(result.Ready);
        Assert.False(result.SteamRunning);
        Assert.Contains("isn't running", result.Detail);
    }

    [Fact]
    public void Workshop_readiness_rejects_missing_registration()
    {
        var result = Evaluate(new Dictionary<int, string> { [100] = "steam" }, null);

        Assert.False(result.Ready);
        Assert.True(result.SteamRunning);
        Assert.Contains("registration is missing", result.Detail);
    }

    [Fact]
    public void Workshop_readiness_rejects_dead_registered_pid()
    {
        var result = Evaluate(new Dictionary<int, string> { [100] = "steam" }, 200);

        Assert.False(result.Ready);
        Assert.Contains("dead PID 200", result.Detail);
    }

    [Fact]
    public void Workshop_readiness_rejects_pid_reused_by_foreign_process()
    {
        var result = Evaluate(
            new Dictionary<int, string> { [100] = "steam", [200] = "notepad" }, 200);

        Assert.False(result.Ready);
        Assert.Contains("belongs to 'notepad'", result.Detail);
    }

    [Fact]
    public void Workshop_readiness_rejects_missing_registered_client_dll()
    {
        var result = SteamLocator.EvaluateWorkshopUploadReadiness(
            new Dictionary<int, string> { [100] = "steam" },
            100,
            ClientDll,
            registeredClientDllExists: false,
            expectedClientDll: ClientDll);

        Assert.False(result.Ready);
        Assert.Contains("steamclient.dll is missing", result.Detail);
    }

    [Fact]
    public void Workshop_readiness_rejects_different_steam_install()
    {
        var result = SteamLocator.EvaluateWorkshopUploadReadiness(
            new Dictionary<int, string> { [100] = "steam" },
            100,
            @"D:\Steam\steamclient.dll",
            registeredClientDllExists: true,
            expectedClientDll: ClientDll);

        Assert.False(result.Ready);
        Assert.Contains("configured for", result.Detail);
    }

    [Fact]
    public void Workshop_readiness_contains_invalid_registered_path()
    {
        var result = SteamLocator.EvaluateWorkshopUploadReadiness(
            new Dictionary<int, string> { [100] = "steam" },
            100,
            "bad\0path",
            registeredClientDllExists: true,
            expectedClientDll: ClientDll);

        Assert.False(result.Ready);
        Assert.Contains("path is invalid", result.Detail);
    }

    [Fact]
    public void Workshop_readiness_accepts_exact_live_registration()
    {
        var result = Evaluate(new Dictionary<int, string> { [100] = "steam" }, 100);

        Assert.True(result.Ready);
        Assert.True(result.SteamRunning);
        Assert.Contains("PID 100", result.Detail);
    }

    private static SteamLocator.UploadReadiness Evaluate(
        IReadOnlyDictionary<int, string> processes,
        int? registeredPid)
        => SteamLocator.EvaluateWorkshopUploadReadiness(
            processes,
            registeredPid,
            ClientDll,
            registeredClientDllExists: true,
            expectedClientDll: ClientDll);
}
