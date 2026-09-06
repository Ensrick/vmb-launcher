# check_noninteractive_contract.ps1 -- prevents default verification from
# launching windows, executing mod actions, mutating default launcher settings,
# or force-stopping an existing launcher process.

[CmdletBinding()]
param(
    [string]$RepoRoot,
    [switch]$SelfTest,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
if (-not $RepoRoot) { $RepoRoot = Split-Path $PSScriptRoot -Parent }

function Get-ContractViolations {
    param([hashtable]$Files)

    $violations = @()
    $headless = [string]$Files['tests/headless_smoke.ps1']
    $publish = [string]$Files['publish.ps1']
    $gui = [string]$Files['tests/gui_smoke.ps1']
    $actions = [string]$Files['tests/action_smoke.ps1']
    $test = [string]$Files['test.ps1']
    $wrapper = [string]$Files['tests/transaction_wrapper_smoke.ps1']

    if ($headless -match '(?im)^\s*(?:&\s*)?Start-Process\b') {
        $violations += 'tests/headless_smoke.ps1 may not call Start-Process'
    }
    if ($headless -match '(?i)(?:^|[\s''"])--gui(?:$|[\s''"])') {
        $violations += 'tests/headless_smoke.ps1 may not exercise --gui'
    }
    if ($headless -match '(?im)\bRun\s+@\(\s*[''"](?:build|deploy|upload|all)[''"]') {
        $violations += 'tests/headless_smoke.ps1 may not execute mutating launcher verbs'
    }
    if ($headless -notmatch '(?m)\$effectiveArgs\s*\+=\s*@\(\s*[''"]--config[''"]\s*,\s*\$ConfigPath\s*\)' -or
        $headless -notmatch '\[IO\.File\]::ReadAllBytes\(\$defaultCfg\)' -or
        $headless -notmatch 'default settings bytes unchanged' -or
        $headless -notmatch 'vmb-headless-smoke-' -or
        $headless -notmatch '(?m)^\s*finally\s*\{') {
        $violations += 'tests/headless_smoke.ps1 must isolate config and prove default settings byte preservation'
    }
    if ($publish -match '(?im)^\s*&[^\r\n]*(?:gui_smoke|action_smoke)\.ps1') {
        $violations += 'publish.ps1 may not invoke interactive or mutating smoke suites'
    }
    if ($publish -notmatch '(?m)^\s*\[switch\]\$OpenOutput\b' -or
        $publish -notmatch '(?m)^\s*if\s*\(\$OpenOutput\)\s*\{') {
        $violations += 'publish.ps1 must make Explorer launch explicit through -OpenOutput'
    }
    if ($publish -match '(?m)^\s*if\s*\(\s*-not\s+\$SkipOpen\s*\)') {
        $violations += 'publish.ps1 must not open Explorer by default'
    }
    $heldGuard = $publish.IndexOf('if (-not $AllowStop)', [StringComparison]::Ordinal)
    $forcedStop = $publish.IndexOf('Stop-Process -Id $p.Id -Force', [StringComparison]::Ordinal)
    if ($publish -notmatch '(?m)^\s*\[switch\]\$ForceStopLauncher\b' -or
        $publish -notmatch 'Prepare-LauncherPublish -AllowStop:\$ForceStopLauncher' -or
        $heldGuard -lt 0 -or $forcedStop -lt $heldGuard) {
        $violations += 'publish.ps1 may stop held launchers only after explicit -ForceStopLauncher'
    }
    if ($gui -notmatch '(?m)^\s*\[switch\]\$Interactive\b' -or
        $gui -notmatch '(?m)^\s*if\s*\(\s*-not\s+\$Interactive\s*\)') {
        $violations += 'tests/gui_smoke.ps1 must require explicit -Interactive consent'
    }
    if ($actions -notmatch '(?m)^\s*\[switch\]\$IntegrationActions\b' -or
        $actions -notmatch '(?m)^\s*if\s*\(\s*-not\s+\$IntegrationActions\s*\)') {
        $violations += 'tests/action_smoke.ps1 must require explicit -IntegrationActions consent'
    }
    if ($test -notmatch 'check_noninteractive_contract\.ps1') {
        $violations += 'test.ps1 must run the noninteractive contract guard'
    }
    if ($test -notmatch 'VmbLauncher\.Tests\.csproj" -c Debug --nologo' -or
        $test -notmatch 'bin\\TestHooks\\Debug\\net9\.0-windows\\VMBLauncher\.exe' -or
        $test -notmatch 'transaction_wrapper_smoke\.ps1" -Exe \$wrapperExe') {
        $violations += 'test.ps1 must pass the exact Debug TestHooks output to the wrapper fixture'
    }
    if ($wrapper -notmatch "'Local\\VMBLauncher\.Tests\.'" -or
        $wrapper -notmatch "TRANSACTION_TEST_MODE = '1'" -or
        $wrapper -notmatch 'TRANSACTION_TEST_MUTEX_NAME = \$mutexName' -or
        $wrapper -notmatch 'TRANSACTION_TEST_MODE = \$old\.TestMode' -or
        $wrapper -notmatch 'TRANSACTION_TEST_MUTEX_NAME = \$old\.TestMutex' -or
        $wrapper -notmatch '\$allowedExecutables -notcontains \$Exe' -or
        $wrapper -notmatch '-WindowStyle Hidden' -or
        $wrapper -notmatch 'Copy\(\$env:ComSpec' -or
        $wrapper -notmatch '& \$Exe build fixture_mod --no-banner --config \$settingsPath' -or
        $wrapper -match '(?i)--gui|&\s+\$Exe\s+(deploy|upload|all)\b|Remove-Item[^\r\n]*-Recurse') {
        $violations += 'wrapper smoke must remain an isolated TestHooks/private-mutex/fake-VMB-only fixture'
    }

    return @($violations)
}

function Read-ContractFiles {
    param([string]$Root)

    $documents = @{}
    foreach ($relative in @(
        'publish.ps1',
        'test.ps1',
        'tests/headless_smoke.ps1',
        'tests/gui_smoke.ps1',
        'tests/action_smoke.ps1',
        'tests/transaction_wrapper_smoke.ps1'
    )) {
        $path = Join-Path $Root ($relative -replace '/', [IO.Path]::DirectorySeparatorChar)
        $documents[$relative] = if (Test-Path -LiteralPath $path -PathType Leaf) {
            [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
        }
        else {
            ''
        }
    }
    return $documents
}

if ($SelfTest) {
    $good = @{
        'publish.ps1' = @'
param(
    [switch]$OpenOutput,
    [switch]$ForceStopLauncher
)
function Prepare-LauncherPublish {
    param([switch]$AllowStop)
    if (-not $AllowStop) {
        throw "held"
    }
    Stop-Process -Id $p.Id -Force
}
Prepare-LauncherPublish -AllowStop:$ForceStopLauncher
if ($OpenOutput) {
    Start-Process explorer.exe
}
'@
        'test.ps1' = @'
& "$root\tests\check_noninteractive_contract.ps1"
dotnet test "$root\tests\VmbLauncher.Tests.csproj" -c Debug --nologo
$wrapperExe = Join-Path $root 'bin\TestHooks\Debug\net9.0-windows\VMBLauncher.exe'
& "$root\tests\transaction_wrapper_smoke.ps1" -Exe $wrapperExe
'@
        'tests/transaction_wrapper_smoke.ps1' = @'
$mutexName = 'Local\VMBLauncher.Tests.' + [guid]::NewGuid().ToString('N')
if ($allowedExecutables -notcontains $Exe) { exit 2 }
[IO.File]::Copy($env:ComSpec, $fakeVmb)
$env:VMBLAUNCHER_TRANSACTION_TEST_MODE = '1'
$env:VMBLAUNCHER_TRANSACTION_TEST_MUTEX_NAME = $mutexName
try {
    & $Exe build fixture_mod --no-banner --config $settingsPath
    Start-Process $probe -WindowStyle Hidden
} finally {
    $env:VMBLAUNCHER_TRANSACTION_TEST_MODE = $old.TestMode
    $env:VMBLAUNCHER_TRANSACTION_TEST_MUTEX_NAME = $old.TestMutex
}
'@
        'tests/headless_smoke.ps1' = @'
$defaultCfgBefore = [IO.File]::ReadAllBytes($defaultCfg)
$ownedConfigRoot = "vmb-headless-smoke-fixture"
try {
    $effectiveArgs += @('--config', $ConfigPath)
    & $Exe list
    Record 'default settings bytes unchanged' $true
}
finally {
    Remove-Item $ownedConfigRoot
}
'@
        'tests/gui_smoke.ps1' = @'
param(
    [switch]$Interactive
)
if (-not $Interactive) {
    exit 2
}
Start-Process $Exe
'@
        'tests/action_smoke.ps1' = @'
param(
    [switch]$IntegrationActions
)
if (-not $IntegrationActions) {
    exit 2
}
Run @('build', 'mod')
'@
    }
    if ((Get-ContractViolations $good).Count -ne 0) {
        throw 'compliant noninteractive fixture was rejected'
    }

    $plantedGui = @{} + $good
    $plantedGui['tests/headless_smoke.ps1'] = 'Start-Process $Exe'
    if ((Get-ContractViolations $plantedGui) -notcontains
        'tests/headless_smoke.ps1 may not call Start-Process') {
        throw 'planted headless Start-Process violation was not detected'
    }

    $plantedAction = @{} + $good
    $plantedAction['tests/headless_smoke.ps1'] = "Run @('deploy', 'mod')"
    if ((Get-ContractViolations $plantedAction) -notcontains
        'tests/headless_smoke.ps1 may not execute mutating launcher verbs') {
        throw 'planted headless deploy violation was not detected'
    }

    $plantedConfig = @{} + $good
    $plantedConfig['tests/headless_smoke.ps1'] = '& $Exe list'
    if ((Get-ContractViolations $plantedConfig) -notcontains
        'tests/headless_smoke.ps1 must isolate config and prove default settings byte preservation') {
        throw 'planted default-settings mutation risk was not detected'
    }

    $plantedDefaultOpen = @{} + $good
    $plantedDefaultOpen['publish.ps1'] = @'
param([switch]$SkipOpen)
if (-not $SkipOpen) {
    Start-Process explorer.exe
}
'@
    if ((Get-ContractViolations $plantedDefaultOpen) -notcontains
        'publish.ps1 must not open Explorer by default') {
        throw 'planted default Explorer launch was not detected'
    }

    $plantedKill = @{} + $good
    $plantedKill['publish.ps1'] = @'
param([switch]$OpenOutput)
Stop-Process -Id $p.Id -Force
if ($OpenOutput) {
    Start-Process explorer.exe
}
'@
    if ((Get-ContractViolations $plantedKill) -notcontains
        'publish.ps1 may stop held launchers only after explicit -ForceStopLauncher') {
        throw 'planted default force-stop was not detected'
    }

    $wrongGraph = @{} + $good
    $wrongGraph['test.ps1'] = $wrongGraph['test.ps1'].Replace('bin\TestHooks\Debug', 'bin\Debug')
    if ((Get-ContractViolations $wrongGraph) -notcontains
        'test.ps1 must pass the exact Debug TestHooks output to the wrapper fixture') {
        throw 'planted stale production-output dependency was not detected'
    }
    foreach ($mutation in @('private-mutex', 'restore-environment', 'visible-probe', 'real-upload')) {
        $unsafeWrapper = @{} + $good
        $text = $unsafeWrapper['tests/transaction_wrapper_smoke.ps1']
        switch ($mutation) {
            'private-mutex' { $text = $text.Replace('Local\VMBLauncher.Tests.', 'Global\Ensrick.VMBLauncher.Transaction.') }
            'restore-environment' { $text = $text.Replace('TRANSACTION_TEST_MODE = $old.TestMode', 'TRANSACTION_TEST_MODE = 1') }
            'visible-probe' { $text = $text.Replace('-WindowStyle Hidden', '') }
            'real-upload' { $text += "`n& `$Exe upload fixture_mod" }
        }
        $unsafeWrapper['tests/transaction_wrapper_smoke.ps1'] = $text
        if ((Get-ContractViolations $unsafeWrapper) -notcontains
            'wrapper smoke must remain an isolated TestHooks/private-mutex/fake-VMB-only fixture') {
            throw "planted unsafe wrapper condition was not detected: $mutation"
        }
    }
    if (-not $Quiet) {
        Write-Host '[check_noninteractive_contract] SELFTEST OK'
    }
    exit 0
}

$violations = @(Get-ContractViolations (Read-ContractFiles $RepoRoot))
if ($violations.Count -gt 0) {
    Write-Host "[check_noninteractive_contract] FAIL -- $($violations.Count) violation(s):"
    foreach ($violation in $violations) { Write-Host "  $violation" }
    exit 2
}

if (-not $Quiet) {
    Write-Host '[check_noninteractive_contract] OK -- default verification is noninteractive and nonmutating.'
}
exit 0
