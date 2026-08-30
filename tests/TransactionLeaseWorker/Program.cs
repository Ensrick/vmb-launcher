using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using VmbLauncher.Services;

static string Arg(string[] args, int index) =>
    index < args.Length ? args[index] : throw new ArgumentException($"missing arg {index}");

var mode = Arg(args, 0);
var semaphore = Arg(args, 1);
var record = Arg(args, 2);
var root = Arg(args, 3);
var marker = Arg(args, 4);
var release = args.Length > 5 ? args[5] : "";
var timeoutMs = args.Length > 6 ? int.Parse(args[6]) : 2000;

if (mode == "receipt-deploy-membership-race")
{
    const string mod = "modx";
    const string publishedId = "123456789";
    const string commit = "0123456789abcdef0123456789abcdef01234567";
    var checkpoint = Arg(args, 7);
    var resultPath = Arg(args, 8);
    using var lease = MachineTransactionLease.Enter(
        mode,
        mod,
        root,
        timeout: TimeSpan.FromMilliseconds(timeoutMs),
        recordPath: record,
        mutexName: semaphore);
    var sourceDirectory = Path.Combine(root, "source");
    var target = Path.Combine(root, "workshop", publishedId);
    var expected = Census(sourceDirectory);
    var authorization = new VerifiedCommitQualifiedExpectedSet(
        mod,
        publishedId,
        commit,
        new string('a', 64),
        DateTime.UtcNow.AddMinutes(30),
        expected);
    using var source = ImmutableBundleSourceLease.Capture(
        sourceDirectory,
        mod,
        expected);
    var trace = new List<string>();
    var gated = false;
    LocalExactSetDeployment.TransitionForTest = point =>
    {
        trace.Add(point);
        if (gated || point != checkpoint) return;
        gated = true;
        WriteAtomicText(marker, $"{Environment.ProcessId}|{point}");
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!File.Exists(release))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"membership-race release timeout at {point}");
            Thread.Sleep(10);
        }
    };
    RunOutcome result;
    try
    {
        result = LocalExactSetDeployment.Reconcile(target, authorization, source);
    }
    finally
    {
        LocalExactSetDeployment.TransitionForTest = null;
    }
    WriteAtomicJson(resultPath, new
    {
        Pid = Environment.ProcessId,
        result.Ok,
        result.Message,
        Trace = trace.ToArray(),
    });
    return result.Ok ? 0 : 93;
}

if (mode == "receipt-deploy-owner-crash")
{
    const string mod = "modx";
    const string publishedId = "123456789";
    const string commit = "0123456789abcdef0123456789abcdef01234567";
    using var lease = MachineTransactionLease.Enter(
        mode,
        mod,
        root,
        timeout: TimeSpan.FromMilliseconds(timeoutMs),
        recordPath: record,
        mutexName: semaphore);
    var sourceDirectory = Path.Combine(root, "source");
    var target = Path.Combine(root, "workshop", publishedId);
    var expected = Census(sourceDirectory);
    var authorization = new VerifiedCommitQualifiedExpectedSet(
        mod,
        publishedId,
        commit,
        new string('a', 64),
        DateTime.UtcNow.AddMinutes(30),
        expected);
    using var source = ImmutableBundleSourceLease.Capture(
        sourceDirectory,
        mod,
        expected);
    var releaseParts = release.Split('@', 2);
    var releaseCheckpoint = releaseParts[0];
    var releaseOccurrence = releaseParts.Length == 2 ? int.Parse(releaseParts[1]) : 1;
    var checkpointOccurrences = 0;
    LocalExactSetDeployment.TransitionForTest = point =>
    {
        if (point != releaseCheckpoint || ++checkpointOccurrences != releaseOccurrence) return;
        WriteAtomicText(marker, point);
        Environment.FailFast($"planted receipt-deploy hard crash at {point}");
    };
    var result = LocalExactSetDeployment.Reconcile(target, authorization, source);
    File.WriteAllText(marker, $"checkpoint-not-reached|{result.Ok}|{result.Message}");
    return 91;
}

if (mode == "receipt-deploy-recover")
{
    const string mod = "modx";
    using var lease = MachineTransactionLease.Enter(
        mode,
        mod,
        root,
        timeout: TimeSpan.FromMilliseconds(timeoutMs),
        recordPath: record,
        mutexName: semaphore);
    var result = LocalExactSetDeployment.RecoverInterruptedSafety(
        Path.Combine(root, "workshop"),
        mod);
    File.WriteAllText(marker, $"{result.Ok}|{result.Message}");
    return result.Ok ? 0 : 92;
}

if (mode == "process-runner-crash")
{
    using var mutation = MachineTransactionLease.Enter(
        "process-runner-crash", "fixture-mod", root,
        recordPath: Path.Combine(root, "process-runner-owner.json"),
        mutexName: @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N"));
    var powershell = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    var command = "$p=[Diagnostics.Process]::GetCurrentProcess();" +
        "$g=Start-Process -FilePath powershell.exe -ArgumentList '-NoLogo','-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 30' -PassThru -WindowStyle Hidden;" +
        $"[IO.File]::WriteAllText('{marker.Replace("'", "''")}',\"$($p.Id),$($g.Id)\");" +
        "Start-Sleep -Seconds 30";
    _ = ProcessRunner.RunAsync(
        powershell,
        new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command },
        root,
        _ => { });
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (!File.Exists(marker))
    {
        if (DateTime.UtcNow > deadline) throw new TimeoutException("contained child marker timeout");
        Thread.Sleep(20);
    }
    Environment.FailFast("planted launcher crash with contained child");
}

if (mode == "owner-crash")
{
    using var lease = MachineTransactionLease.Enter(
        "owner-crash", "fixture-mod", root,
        timeout: TimeSpan.FromMilliseconds(timeoutMs),
        recordPath: record, mutexName: semaphore);
    var identity = MachineTransactionLease.CurrentIdentity!;
    Environment.SetEnvironmentVariable(
        MachineTransactionLease.LeaseIdEnvironmentVariable, identity.LeaseId);
    Environment.SetEnvironmentVariable(
        MachineTransactionLease.OwnerPidEnvironmentVariable, identity.OwnerPid.ToString());
    Environment.SetEnvironmentVariable(
        MachineTransactionLease.OwnerStartEnvironmentVariable, identity.OwnerStartUtcTicks.ToString());
    Environment.SetEnvironmentVariable(
        MachineTransactionLease.RecordPathEnvironmentVariable, record);

    var childMarker = marker + ".child";
    var child = Process.Start(new ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        UseShellExecute = false,
        CreateNoWindow = true,
        ArgumentList =
        {
            "child", semaphore, record, root, childMarker, release, "2000"
        },
    }) ?? throw new InvalidOperationException("could not start joined child");
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (!File.Exists(childMarker))
    {
        if (child.HasExited) throw new InvalidOperationException("joined child exited before marker");
        if (DateTime.UtcNow > deadline) throw new TimeoutException("joined child marker timeout");
        Thread.Sleep(20);
    }
    File.WriteAllText(marker, File.ReadAllText(childMarker));
    var crashTrigger = marker + ".crash";
    deadline = DateTime.UtcNow.AddSeconds(10);
    while (!File.Exists(crashTrigger))
    {
        if (DateTime.UtcNow > deadline) throw new TimeoutException("owner crash trigger timeout");
        Thread.Sleep(20);
    }
    Environment.FailFast("planted owner crash after child joined");
}

if (mode == "owner-crash-simple")
{
    using var lease = MachineTransactionLease.Enter(
        "owner-crash-simple", "fixture-mod", root,
        timeout: TimeSpan.FromMilliseconds(timeoutMs),
        recordPath: record, mutexName: semaphore);
    File.WriteAllText(marker, "acquired");
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (!File.Exists(release))
    {
        if (DateTime.UtcNow > deadline) throw new TimeoutException("owner crash trigger timeout");
        Thread.Sleep(20);
    }
    Environment.FailFast("planted simple owner crash");
}

if (mode is "settings-owner" or "settings-contender")
{
    using var lease = MachineTransactionLease.Enter(
        mode, mod: null, projectRoot: null,
        timeout: TimeSpan.FromMilliseconds(timeoutMs),
        recordPath: record, mutexName: semaphore);
    var settingsPath = Path.Combine(root, "settings.json");
    // The important ordering: reload only after the machine lease is owned.
    var settings = Settings.Load(settingsPath);
    if (mode == "settings-owner")
    {
        File.WriteAllText(marker, "acquired");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!File.Exists(release))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("settings owner release timeout");
            Thread.Sleep(20);
        }
        settings.VmbRoot = "owner-write";
    }
    else
    {
        settings.SteamRoot = "contender-write";
    }
    settings.Save();
    if (mode == "settings-contender") File.WriteAllText(marker, "saved");
    return 0;
}

using (var lease = MachineTransactionLease.Enter(
    mode, "fixture-mod", root,
    timeout: TimeSpan.FromMilliseconds(timeoutMs),
    recordPath: record, mutexName: semaphore))
{
    if (mode == "contender-require-dead")
    {
        foreach (var text in File.ReadAllText(release).Split(','))
        {
            var pid = int.Parse(text);
            try
            {
                using var live = Process.GetProcessById(pid);
                if (!live.HasExited)
                    throw new InvalidOperationException($"descendant PID {pid} was live when contender entered");
            }
            catch (ArgumentException) { }
        }
    }
    if (mode == "owner-success-residue")
    {
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var command = "$g=Start-Process powershell.exe -ArgumentList '-NoLogo','-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 30' -PassThru -WindowStyle Hidden;" +
            $"[IO.File]::WriteAllText('{marker.Replace("'", "''")}',\"$PID,$($g.Id)\")";
        using var child = Process.Start(new ProcessStartInfo
        {
            FileName = powershell,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command },
        }) ?? throw new InvalidOperationException("could not start residue fixture child");
        var markerDeadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(marker))
        {
            if (child.HasExited && DateTime.UtcNow > markerDeadline)
                throw new TimeoutException("residue marker timeout");
            Thread.Sleep(20);
        }
        var releaseDeadline = DateTime.UtcNow.AddSeconds(20);
        while (!File.Exists(release))
        {
            if (DateTime.UtcNow > releaseDeadline) throw new TimeoutException("residue release timeout");
            Thread.Sleep(20);
        }
    }
    else if (mode != "child") File.WriteAllText(marker, "acquired");
    if (mode == "child")
    {
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        using var grandchild = Process.Start(new ProcessStartInfo
        {
            FileName = powershell,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" },
        }) ?? throw new InvalidOperationException("could not start contained grandchild");
        File.WriteAllText(marker, $"{Environment.ProcessId},{grandchild.Id}");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!File.Exists(release))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("child release timeout");
            Thread.Sleep(20);
        }
    }
}

return 0;

static IReadOnlyList<CommitQualifiedOutputFile> Census(string directory) =>
    Directory.EnumerateFiles(directory)
        .Select(path =>
        {
            var bytes = File.ReadAllBytes(path);
            return new CommitQualifiedOutputFile(
                Path.GetFileName(path),
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        })
        .OrderBy(file => file.Name, StringComparer.Ordinal)
        .ToArray();

static void WriteAtomicJson(string path, object value)
    => WriteAtomicText(path, JsonSerializer.Serialize(value));

static void WriteAtomicText(string path, string value)
{
    var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
    File.WriteAllText(temporary, value);
    File.Move(temporary, path);
}
