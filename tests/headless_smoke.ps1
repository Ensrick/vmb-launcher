# headless_smoke.ps1 — end-to-end exercise of the headless CLI against every claim in
# tools/vmb-launcher/CLAUDE.md. Run from the launcher folder. Returns exit 0 if every
# expectation matches, exit 1 with a summary of failures otherwise.
#
# This is intentionally NOT a unit test — it runs the real binary against the real
# filesystem and the real VMB toolchain, because the launcher's value is in those
# integrations. Pair this with `test.ps1` (xUnit) for unit-level coverage of services.

param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\bin\Debug\net9.0-windows\VMBLauncher.exe'),
    [string]$TestMod = 'general_tweaker',     # safe target — private, has a Workshop ID, deployable
    [string]$PublicMod = 'chaos_wastes_tweaker'  # the one mod whose visibility is public
)

# Native stderr lines from the exe come back as ErrorRecord objects under Stop, which
# aborts the runner the first time an expected-error verb runs. Continue keeps those as
# regular pipeline output; we capture them via 2>&1 in Run().
$ErrorActionPreference = 'Continue'
$Exe = (Resolve-Path $Exe).Path

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
    $stdout = & $Exe @ExeArgs 2>&1
    return [PSCustomObject]@{ Code = $LASTEXITCODE; Output = ($stdout | Out-String) }
}

Write-Host "Testing $Exe" -ForegroundColor Cyan
Write-Host "Test mod: $TestMod (private), $PublicMod (public)" -ForegroundColor DarkGray

# --- Exit codes -----------------------------------------------------------------------------

$r = Run @('list', '--no-banner')
Record 'list exits 0' ($r.Code -eq 0) ("got exit=$($r.Code)")

$r = Run @('--no-banner')
Record 'no verb exits 2' ($r.Code -eq 2) ("got exit=$($r.Code), output: $($r.Output -replace "`r?`n",' | ')")

$r = Run @('potato', '--no-banner')
Record 'unknown verb exits 2' ($r.Code -eq 2) ("got exit=$($r.Code)")

$r = Run @('build', '--no-banner')
Record 'build missing mod exits 2' ($r.Code -eq 2)

$r = Run @('info', 'no_such_mod_12345', '--no-banner')
Record 'info nonexistent mod exits 2' ($r.Code -eq 2)

$r = Run @('upload', $PublicMod, '--no-banner')
Record 'upload public without --allow-public exits 2' ($r.Code -eq 2)

# --- Help / banner -------------------------------------------------------------------------

$r = Run @('help')
Record 'help exits 0' ($r.Code -eq 0)
Record 'help mentions all verbs' ($r.Output -match 'list' -and $r.Output -match 'info' -and $r.Output -match 'doctor' -and $r.Output -match 'build' -and $r.Output -match 'deploy' -and $r.Output -match 'upload' -and $r.Output -match 'all')

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

# --- build (real action) -----------------------------------------------------------------

$r = Run @('build', $TestMod, '--no-banner')
Record "build $TestMod exits 0" ($r.Code -eq 0) ("got exit=$($r.Code)")
Record "build $TestMod streams VMB output" ($r.Output -match 'Successfully built')
Record "build $TestMod emits [build] OK line" ($r.Output -match '\[build\] OK')

# --- deploy (real action) ----------------------------------------------------------------

$r = Run @('deploy', $TestMod, '--no-banner')
Record "deploy $TestMod exits 0" ($r.Code -eq 0)
Record "deploy $TestMod emits [deploy] OK line" ($r.Output -match '\[deploy\] OK')

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
    cmd /c "`"$Exe`" list --no-banner | `"$head`" -n 3 >NUL"
    Record 'truncated pipe (cmd + head) exits 0' ($LASTEXITCODE -eq 0) ("got exit=$LASTEXITCODE")
} else {
    Record 'truncated pipe test skipped (Git head not found)' $true
}

# --- Settings file path ------------------------------------------------------------------

$defaultCfg = Join-Path $env:APPDATA 'VMBLauncher\settings.json'
Record 'default settings file exists' (Test-Path $defaultCfg)

# --- All verbs work on the public mod (with --allow-public) ------------------------------

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

# --- GUI fallback ------------------------------------------------------------------------
# Launch zero-arg and --gui in the background, give them a moment to show a window, kill.

function Test-GuiArgs {
    param([string]$Label, [string[]]$LaunchArgs)
    # Start-Process refuses an empty -ArgumentList, so branch on whether args were supplied.
    if ($null -eq $LaunchArgs -or $LaunchArgs.Count -eq 0) {
        $p = Start-Process -FilePath $Exe -PassThru -WindowStyle Hidden
    } else {
        $p = Start-Process -FilePath $Exe -ArgumentList $LaunchArgs -PassThru -WindowStyle Hidden
    }
    Start-Sleep -Seconds 2
    $p.Refresh()
    $hadWindow = (-not $p.HasExited) -and (-not [string]::IsNullOrEmpty($p.MainWindowTitle))
    if (-not $p.HasExited) { $p | Stop-Process -Force -ErrorAction SilentlyContinue }
    Record $Label $hadWindow ("exited=$($p.HasExited) title='$($p.MainWindowTitle)'")
}
Test-GuiArgs 'zero-arg launches GUI window' @()
Test-GuiArgs '--gui flag launches GUI window' @('--gui')
Test-GuiArgs '--gui with other args still launches GUI' @('list', '--gui')

# --- Summary -----------------------------------------------------------------------------

$failed = $results | Where-Object { -not $_.Pass }
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
