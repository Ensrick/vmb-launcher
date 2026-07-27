# action_smoke.ps1 -- explicit opt-in integration exercise for launcher actions.
#
# This suite builds and deploys a real mod on the local machine. It is never
# called by publish.ps1, test.ps1, CI, or the default headless smoke suite.

[CmdletBinding()]
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\bin\Debug\net9.0-windows\VMBLauncher.exe'),
    [string]$ConfigPath = '',
    [string]$TestMod = 'general_tweaker',
    [string]$PublicMod = 'chaos_wastes_tweaker',
    [switch]$IntegrationActions
)

$ErrorActionPreference = 'Continue'

if (-not $IntegrationActions) {
    Write-Error 'Action smoke mutates local build/deploy state. Re-run with -IntegrationActions only when intended.'
    exit 2
}

$Exe = (Resolve-Path -LiteralPath $Exe).Path
$results = @()

function Record {
    param([string]$Name, [bool]$Pass, [string]$Detail = '')
    $script:results += [pscustomobject]@{ Name = $Name; Pass = $Pass; Detail = $Detail }
}

function Run {
    param([string[]]$ExeArgs)
    $effectiveArgs = @($ExeArgs)
    if (-not [string]::IsNullOrWhiteSpace($ConfigPath)) {
        $effectiveArgs += @('--config', $ConfigPath)
    }
    $stdout = & $Exe @effectiveArgs 2>&1
    return [pscustomobject]@{ Code = $LASTEXITCODE; Output = ($stdout | Out-String) }
}

# These publication probes must fail before ugc_tool. They remain here because
# even invoking a publication verb is outside the default verification contract.
$result = Run @('build', '--no-banner')
Record 'build missing mod exits 2' ($result.Code -eq 2)

$result = Run @('upload', $PublicMod, '--no-banner')
Record 'public upload without --allow-public exits 2' ($result.Code -eq 2)

$result = Run @('upload', $TestMod, '--allow-public', '--no-banner')
Record 'direct upload without hosted receipt exits 3' ($result.Code -eq 3) $result.Output
Record 'direct upload explains claim alone is insufficient' ($result.Output -match 'claim\s+alone')

$result = Run @('upload', $TestMod, '--allow-public', '--no-claim', '--no-banner')
Record '--no-claim is rejected as unknown' ($result.Code -eq 2 -and $result.Output -match 'unknown')

$result = Run @('build', $TestMod, '--no-banner')
Record "build $TestMod exits 0" ($result.Code -eq 0) $result.Output
Record "build $TestMod streams VMB output" ($result.Output -match 'Successfully built')
Record "build $TestMod emits [build] OK" ($result.Output -match '\[build\] OK')

# Explicitly keep remote machines out of this local integration suite.
$result = Run @('deploy', $TestMod, '--no-remote', '--no-banner')
Record "deploy $TestMod exits 0" ($result.Code -eq 0) $result.Output
Record "deploy $TestMod emits [deploy] OK" ($result.Output -match '\[deploy\] OK')

$failed = @($results | Where-Object { -not $_.Pass })
foreach ($entry in $results) {
    $tag = if ($entry.Pass) { 'PASS' } else { 'FAIL' }
    Write-Host "[$tag] $($entry.Name)"
    if (-not $entry.Pass -and $entry.Detail) { Write-Host "       $($entry.Detail)" }
}

if ($failed.Count -gt 0) { exit 1 }
exit 0
