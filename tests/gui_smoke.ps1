# gui_smoke.ps1 -- explicit opt-in interactive verification of GUI routing.
#
# This suite creates visible VMBLauncher windows. It is never called by
# publish.ps1, test.ps1, CI, or the headless smoke suite.

[CmdletBinding()]
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\bin\Debug\net9.0-windows\VMBLauncher.exe'),
    [switch]$Interactive
)

$ErrorActionPreference = 'Stop'

if (-not $Interactive) {
    Write-Error 'GUI smoke is interactive. Re-run with -Interactive only when visible windows are intended.'
    exit 2
}

$Exe = (Resolve-Path -LiteralPath $Exe).Path
$results = @()

function Test-GuiArgs {
    param([string]$Label, [string[]]$LaunchArgs)

    if ($null -eq $LaunchArgs -or $LaunchArgs.Count -eq 0) {
        $process = Start-Process -FilePath $Exe -PassThru
    }
    else {
        $process = Start-Process -FilePath $Exe -ArgumentList $LaunchArgs -PassThru
    }

    try {
        Start-Sleep -Seconds 2
        $process.Refresh()
        $hadWindow = (-not $process.HasExited) -and
            (-not [string]::IsNullOrEmpty($process.MainWindowTitle))
        $script:results += [pscustomobject]@{
            Label = $Label
            Pass = $hadWindow
            Detail = "exited=$($process.HasExited) title='$($process.MainWindowTitle)'"
        }
    }
    finally {
        if (-not $process.HasExited) {
            $process | Stop-Process -Force -ErrorAction SilentlyContinue
        }
    }
}

Test-GuiArgs 'zero-arg launches GUI window' @()
Test-GuiArgs '--gui flag launches GUI window' @('--gui')
Test-GuiArgs '--gui with other args still launches GUI' @('list', '--gui')

$failed = @($results | Where-Object { -not $_.Pass })
foreach ($result in $results) {
    $tag = if ($result.Pass) { 'PASS' } else { 'FAIL' }
    Write-Host "[$tag] $($result.Label)"
    if (-not $result.Pass) { Write-Host "       $($result.Detail)" }
}

if ($failed.Count -gt 0) { exit 1 }
exit 0
