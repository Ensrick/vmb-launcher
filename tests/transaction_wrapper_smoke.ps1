# Runs the real launcher transaction-join boundary with a fake VMB executable
# and isolated settings. No real VMB, Stingray, deploy, upload, SDK staging,
# Steam, Workshop, or GUI action is reachable.

param([string]$Exe = (Join-Path $PSScriptRoot '..\bin\Debug\net9.0-windows\VMBLauncher.exe'))
$ErrorActionPreference = 'Continue'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('vmb-wrapper-join-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temp) | Out-Null
$mutexName = 'Local\VMBLauncher.Tests.' + [guid]::NewGuid().ToString('N')
$recordPath = Join-Path $temp 'transaction.json'
$old = @{
    LeaseId = $env:VMBLAUNCHER_TRANSACTION_LEASE_ID
    OwnerPid = $env:VMBLAUNCHER_TRANSACTION_OWNER_PID
    OwnerStart = $env:VMBLAUNCHER_TRANSACTION_OWNER_START_UTC_TICKS
    RecordPath = $env:VMBLAUNCHER_TRANSACTION_RECORD_PATH
    TestMode = $env:VMBLAUNCHER_TRANSACTION_TEST_MODE
    TestMutex = $env:VMBLAUNCHER_TRANSACTION_TEST_MUTEX_NAME
}
$mutex = New-Object System.Threading.Mutex($false, $mutexName)
$acquired = $false
try {
    try { $acquired = $mutex.WaitOne(5000) }
    catch [System.Threading.AbandonedMutexException] { $acquired = $true }
    if (-not $acquired) { throw 'test could not acquire production transaction mutex' }

    $vmbRoot = Join-Path $temp 'fake-vmb'
    $project = Join-Path $temp 'project'
    $mods = Join-Path $project 'mods'
    $mod = Join-Path $mods 'fixture_mod'
    [IO.Directory]::CreateDirectory($vmbRoot) | Out-Null
    [IO.Directory]::CreateDirectory($mod) | Out-Null
    [IO.File]::Copy($env:ComSpec, (Join-Path $vmbRoot 'vmb.exe'))
    [IO.File]::WriteAllText((Join-Path $project '.vmbrc'), '{"mods_dir":"mods"}')
    [IO.File]::WriteAllText((Join-Path $mod 'itemV2.cfg'), 'title = "Fixture"; visibility = "private"; published_id = 1L;')
    $settings = @{
        VmbRoot = $vmbRoot
        ProjectRoot = $project
        SteamRoot = $temp
        Vt2SdkRoot = $temp
        UgcToolPath = (Join-Path $temp 'missing-ugc.exe')
        WorkshopContentRoot = $temp
        WorkshopIdOverrides = @{}
        RemoteDeployTargets = @()
        ConfirmedFirstRun = $true
    } | ConvertTo-Json -Depth 6
    $settingsPath = Join-Path $temp 'settings.json'
    [IO.File]::WriteAllText($settingsPath, $settings, (New-Object Text.UTF8Encoding($false)))

    $process = [Diagnostics.Process]::GetCurrentProcess()
    $leaseId = [guid]::NewGuid().ToString('N')
    $startTicks = $process.StartTime.ToUniversalTime().Ticks
    $record = @{
        schema = 2
        lease_id = $leaseId
        owner_pid = $PID
        owner_start_utc_ticks = $startTicks
        session_id = $process.SessionId
        action = 'ship-fixture'
        mod = 'fixture_mod'
        project_root = $project
        acquired_utc = [DateTime]::UtcNow.ToString('o')
        process_tree_job_name = 'Global\Ensrick.VMBLauncher.Transaction.Process.fixture'
    } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($recordPath, $record, (New-Object Text.UTF8Encoding($false)))
    $env:VMBLAUNCHER_TRANSACTION_LEASE_ID = $leaseId
    $env:VMBLAUNCHER_TRANSACTION_OWNER_PID = "$PID"
    $env:VMBLAUNCHER_TRANSACTION_OWNER_START_UTC_TICKS = "$startTicks"
    $env:VMBLAUNCHER_TRANSACTION_RECORD_PATH = $recordPath
    $env:VMBLAUNCHER_TRANSACTION_TEST_MODE = '1'
    $env:VMBLAUNCHER_TRANSACTION_TEST_MUTEX_NAME = $mutexName

    $output = & $Exe build fixture_mod --no-banner --config $settingsPath 2>&1 | Out-String
    if ($output -notmatch '\[transaction-lease\] joined owner_pid=') {
        throw "real launcher did not authenticate/join wrapper ownership: $output"
    }
    if ($output -match [regex]::Escape($leaseId)) {
        throw 'transaction diagnostics leaked the secret lease id'
    }
    # Mutex acquisition is thread-reentrant, so probing again on this owning
    # PowerShell thread is meaningless. Use a separate process and require it
    # to observe BUSY while this wrapper remains the owner.
    $probeScript = Join-Path $temp 'probe-mutex.ps1'
    [IO.File]::WriteAllText($probeScript, @'
param([string]$Name)
$m = [Threading.Mutex]::OpenExisting($Name)
$got = $false
try {
    try { $got = $m.WaitOne(0) } catch [Threading.AbandonedMutexException] { $got = $true }
    if ($got) { exit 2 }
    exit 0
}
finally {
    if ($got) { try { $m.ReleaseMutex() } catch { } }
    $m.Dispose()
}
'@, (New-Object Text.UTF8Encoding($false)))
    $probeProcess = Start-Process -FilePath powershell.exe -ArgumentList @(
        '-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
        '-File', $probeScript, '-Name', $mutexName
    ) -PassThru -Wait -WindowStyle Hidden
    if ($probeProcess.ExitCode -ne 0) {
        throw 'wrapper-owned mutex became acquirable while the parent transaction was live'
    }
    Write-Host '[transaction_wrapper_smoke] PASS - real launcher joined its exact live parent; fake VMB only.' -ForegroundColor Green
    exit 0
}
catch {
    Write-Host "[transaction_wrapper_smoke] FAILED: $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}
finally {
    $env:VMBLAUNCHER_TRANSACTION_LEASE_ID = $old.LeaseId
    $env:VMBLAUNCHER_TRANSACTION_OWNER_PID = $old.OwnerPid
    $env:VMBLAUNCHER_TRANSACTION_OWNER_START_UTC_TICKS = $old.OwnerStart
    $env:VMBLAUNCHER_TRANSACTION_RECORD_PATH = $old.RecordPath
    $env:VMBLAUNCHER_TRANSACTION_TEST_MODE = $old.TestMode
    $env:VMBLAUNCHER_TRANSACTION_TEST_MUTEX_NAME = $old.TestMutex
    if ($acquired) { try { $mutex.ReleaseMutex() } catch { } }
    $mutex.Dispose()
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
