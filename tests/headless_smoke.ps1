# headless_smoke.ps1 — read-only exercise of the headless CLI contract documented in
# CLAUDE.md. Run from the launcher folder. Returns exit 0 if every expectation
# matches, exit 1 with a summary of failures otherwise.
#
# This is intentionally NOT a unit test — it runs the real binary against read-only
# launcher discovery and diagnostics. It never invokes a build, deploy, upload, or
# GUI path. Pair it with `test.ps1` (xUnit) for unit-level coverage of services.

param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\bin\Debug\net9.0-windows\VMBLauncher.exe'),
    [string]$ConfigPath = '',
    [string]$TestMod = 'general_tweaker',
    [string]$PublicMod = 'chaos_wastes_tweaker'
)

# Native stderr lines from the exe come back as ErrorRecord objects under Stop, which
# aborts the runner the first time an expected-error verb runs. Continue keeps those as
# regular pipeline output; we capture them via 2>&1 in Run().
$ErrorActionPreference = 'Continue'
$Exe = (Resolve-Path $Exe).Path

function Test-BytesEqual {
    param([byte[]]$Left, [byte[]]$Right)
    if ($null -eq $Left -or $null -eq $Right) { return $null -eq $Left -and $null -eq $Right }
    if ($Left.Length -ne $Right.Length) { return $false }
    for ($i = 0; $i -lt $Left.Length; $i++) {
        if ($Left[$i] -ne $Right[$i]) { return $false }
    }
    return $true
}

$defaultCfg = Join-Path $env:APPDATA 'VMBLauncher\settings.json'
$defaultCfgExisted = Test-Path -LiteralPath $defaultCfg -PathType Leaf
$defaultCfgBefore = if ($defaultCfgExisted) {
    [IO.File]::ReadAllBytes($defaultCfg)
}
else {
    $null
}
$ownedConfigRoot = $null
if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ownedConfigRoot = Join-Path ([IO.Path]::GetTempPath()) ("vmb-headless-smoke-{0}" -f [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $ownedConfigRoot | Out-Null
    $ConfigPath = Join-Path $ownedConfigRoot 'settings.json'
    if ($defaultCfgExisted) {
        [IO.File]::WriteAllBytes($ConfigPath, $defaultCfgBefore)
    }
    else {
        [IO.File]::WriteAllText($ConfigPath, '{}', (New-Object Text.UTF8Encoding($false)))
    }
}

$results = @()
function Record {
    param([string]$Name, [bool]$Pass, [string]$Detail = '')
    $script:results += [PSCustomObject]@{ Name = $Name; Pass = $Pass; Detail = $Detail }
    $tag = if ($Pass) { 'PASS' } else { 'FAIL' }
    $color = if ($Pass) { 'Green' } else { 'Red' }
    Write-Host ("[{0}] {1}" -f $tag, $Name) -ForegroundColor $color
    if (-not $Pass -and $Detail) { Write-Host "        $Detail" -ForegroundColor DarkRed }
}

function Run {
    param([string[]]$ExeArgs)
    $effectiveArgs = @($ExeArgs)
    $effectiveArgs += @('--config', $ConfigPath)
    $stdout = & $Exe @effectiveArgs 2>&1
    return [PSCustomObject]@{ Code = $LASTEXITCODE; Output = ($stdout | Out-String) }
}

try {
    Write-Host "Testing $Exe" -ForegroundColor Cyan
    Write-Host "Test mod: $TestMod; public discovery sample: $PublicMod" -ForegroundColor DarkGray

# --- Exit codes -----------------------------------------------------------------------------

$r = Run @('list', '--no-banner')
Record 'list exits 0' ($r.Code -eq 0) ("got exit=$($r.Code)")

$r = Run @('--no-banner')
Record 'no verb exits 2' ($r.Code -eq 2) ("got exit=$($r.Code), output: $($r.Output -replace "`r?`n",' | ')")

$r = Run @('potato', '--no-banner')
Record 'unknown verb exits 2' ($r.Code -eq 2) ("got exit=$($r.Code)")

$r = Run @('info', 'no_such_mod_12345', '--no-banner')
Record 'info nonexistent mod exits 2' ($r.Code -eq 2)

# --- Help / banner -------------------------------------------------------------------------

$r = Run @('capabilities', '--no-banner')
Record 'capabilities exits 0' ($r.Code -eq 0)
Record 'capabilities advertises receipt schema 3' ($r.Output -match 'publication_receipt_schema=3')
Record 'capabilities advertises deployment receipt schema 3' ($r.Output -match 'deployment_receipt_schema=3')
Record 'capabilities advertises locked upload snapshot' ($r.Output -match 'locked-upload-snapshot-v1')
Record 'capabilities advertises exact Git commit blobs' ($r.Output -match 'git-commit-blob-snapshot-v1')
Record 'capabilities advertises constrained first-upload bootstrap' ($r.Output -match 'constrained-first-upload-bootstrap-v1')
Record 'capabilities advertises machine transaction lease' ($r.Output -match 'machine-transaction-lease-v1')
Record 'capabilities advertises crash-safe upload ACL journal' ($r.Output -match 'crash-safe-upload-acl-journal-v1')
Record 'capabilities advertises receipt-authority publication' ($r.Output -match 'receipt-authority-publication-v1')
Record 'capabilities advertises receipt-authority local deploy' ($r.Output -match 'receipt-authority-local-deploy-v1')

$r = Run @('help')
Record 'help exits 0' ($r.Code -eq 0)
Record 'help mentions all verbs' ($r.Output -match 'list' -and $r.Output -match 'info' -and $r.Output -match 'doctor' -and $r.Output -match 'build' -and $r.Output -match 'deploy' -and $r.Output -match 'upload' -and $r.Output -match 'all')
Record 'help documents the distinct deployment receipt input' ($r.Output -match '--deployment-receipt')

$r = Run @('--help')
Record '--help exits 0' ($r.Code -eq 0)

$r = Run @('-h')
Record '-h exits 0' ($r.Code -eq 0)

$r = Run @('list')
Record 'banner present without --no-banner' ($r.Output -match 'vmblauncher \d')

$r = Run @('list', '--no-banner')
Record 'banner absent with --no-banner' (-not ($r.Output -match 'vmblauncher \d'))

# --- list --------------------------------------------------------------------------------

$r = Run @('list', '--no-banner')
Record "list contains $TestMod" ($r.Output -match [regex]::Escape($TestMod))
Record 'list contains a header row' ($r.Output -match 'NAME\s+VISIBILITY\s+WORKSHOP_ID\s+BUILT')

# --- info --------------------------------------------------------------------------------

$r = Run @('info', $TestMod, '--no-banner')
Record "info $TestMod exits 0" ($r.Code -eq 0)
Record "info $TestMod has Visibility field" ($r.Output -match 'Visibility:')
Record "info $TestMod has Workshop ID" ($r.Output -match 'Workshop ID:')

# --- doctor ------------------------------------------------------------------------------

$r = Run @('doctor', '--no-banner')
$diagOk = ($r.Code -eq 0 -or $r.Code -eq 3)   # ok or preflight depending on env
Record 'doctor returns 0 or 3' $diagOk ("got exit=$($r.Code)")
Record 'doctor reports VMB check' ($r.Output -match 'VMB:')
Record 'doctor reports Steam check' ($r.Output -match 'Steam:')
Record 'doctor reports SDK check' ($r.Output -match 'Vermintide 2 SDK:')

# --- GUI detection rules -----------------------------------------------------------------
# We DON'T actually launch the GUI (would block); we verify the headless branch is taken
# by checking output is produced for these arg shapes.

$r = Run @('--no-banner', 'list')   # --no-banner before verb
Record 'args reorderable: --no-banner before verb' ($r.Code -eq 0 -and $r.Output -match $TestMod)

# --- Broken pipe behaviour ---------------------------------------------------------------
# cmd's pipe is the easiest way to verify true exit code under truncation (PowerShell's
# pipeline truncation sets $LASTEXITCODE=-1 regardless — documented quirk, not testable).

$head = 'C:\Program Files\Git\usr\bin\head.exe'
if (Test-Path $head) {
    $configArg = if ([string]::IsNullOrWhiteSpace($ConfigPath)) { '' } else { " --config `"$ConfigPath`"" }
    cmd /c "`"$Exe`" list --no-banner$configArg | `"$head`" -n 3 >NUL"
    Record 'truncated pipe (cmd + head) exits 0' ($LASTEXITCODE -eq 0) ("got exit=$LASTEXITCODE")
} else {
    Record 'truncated pipe test skipped (Git head not found)' $true
}

# --- Settings file path ------------------------------------------------------------------

Record 'isolated settings file exists' (Test-Path -LiteralPath $ConfigPath)

# --- Read-only discovery works on a public mod --------------------------------------------

$r = Run @('info', $PublicMod, '--no-banner')
Record "info on public mod ($PublicMod) exits 0" ($r.Code -eq 0)

# --- Cross-mod coverage: info on every discovered mod ------------------------------------

$listOut = (Run @('list', '--no-banner')).Output
$mods = $listOut -split "`r?`n" |
    Where-Object { $_ -and ($_ -notmatch '^NAME') -and ($_ -notmatch '^----') } |
    ForEach-Object { ($_ -split '\s+')[0] } |
    Where-Object { $_ -and $_ -ne '' }
$infoFailures = @()
foreach ($m in $mods) {
    $rr = Run @('info', $m, '--no-banner')
    if ($rr.Code -ne 0) { $infoFailures += $m }
}
Record "info works on every discovered mod ($($mods.Count) total)" ($infoFailures.Count -eq 0) ("failed: $($infoFailures -join ', ')")

# --- Path-agnostic invocation ------------------------------------------------------------

Push-Location $env:TEMP
try {
    $r = Run @('list', '--no-banner')
    Record 'launcher works from arbitrary cwd' ($r.Code -eq 0)
} finally {
    Pop-Location
}

# --- Default settings preservation --------------------------------------------------------

$defaultCfgAfterExists = Test-Path -LiteralPath $defaultCfg -PathType Leaf
$defaultCfgAfter = if ($defaultCfgAfterExists) {
    [IO.File]::ReadAllBytes($defaultCfg)
}
else {
    $null
}
Record 'default settings bytes unchanged' (
    $defaultCfgExisted -eq $defaultCfgAfterExists -and
    (Test-BytesEqual $defaultCfgBefore $defaultCfgAfter)
)
}
finally {
    if ($ownedConfigRoot) {
        if (Test-Path -LiteralPath $ConfigPath -PathType Leaf) {
            Remove-Item -LiteralPath $ConfigPath -Force
        }
        if (Test-Path -LiteralPath $ownedConfigRoot -PathType Container) {
            Remove-Item -LiteralPath $ownedConfigRoot
        }
    }
}

# --- Summary -----------------------------------------------------------------------------

$failed = @($results | Where-Object { -not $_.Pass })
Write-Host ''
Write-Host ("Total: {0}    Pass: {1}    Fail: {2}" -f $results.Count, ($results.Count - $failed.Count), $failed.Count) `
    -ForegroundColor ($(if ($failed.Count -eq 0) { 'Green' } else { 'Red' }))

if ($failed.Count -gt 0) {
    Write-Host 'Failures:' -ForegroundColor Red
    foreach ($f in $failed) {
        Write-Host "  $($f.Name) -- $($f.Detail)" -ForegroundColor Red
    }
    exit 1
}
exit 0
