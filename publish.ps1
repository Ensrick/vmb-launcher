# publish.ps1 -- builds VMBLauncher.exe as a self-contained, single-file Windows binary.
# Output: bin\Release\net9.0-windows\win-x64\publish\VMBLauncher.exe
#
# This is the file you ship to friends. No .NET install required on their machine.
# ~70 MB self-extracting exe; ~30-40 MB after compression. Cold-start ~1-2s.
#
# Usage:
#   .\publish.ps1                # tests + build + headless smoke; opens nothing
#   .\publish.ps1 -OpenOutput    # same, then explicitly opens the output folder
#   .\publish.ps1 -ForceStopLauncher # explicitly terminate a held launcher

param(
    [switch]$OpenOutput,
    [switch]$ForceStopLauncher,
    # Backward-compatible no-op. The default became noninteractive in #1025.
    [switch]$SkipOpen
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

if ($OpenOutput -and $SkipOpen) {
    throw "-OpenOutput and the deprecated -SkipOpen switch cannot be combined."
}

# Auto-stop any running VMBLauncher.exe before publishing.
#
# `dotnet publish --self-contained -p:PublishSingleFile=true` writes a new
# VMBLauncher.exe over the previous one. On Windows, replacing a running .exe
# fails with `MSB4018: The process cannot access the file ... because it is
# being used by another process` — either the GUI is still open, or a long-
# running CLI verb hasn't returned. Forcing a manual close would mean
# `publish.ps1` can't be run command-line without GUI babysitting, which
# contradicts the launcher's "headless first" doctrine.
#
# Called immediately before `dotnet publish`, not at script start — the test
# suite runs ~30 s, plenty of time for a user (or autorestart-on-crash GUI)
# to relaunch the binary in the window between an early check and the actual
# overwrite. Burned 2026-05-24: first attempt put the check at script start
# and a fresh VMBLauncher PID appeared 2 minutes later, before publish ran.
function Prepare-LauncherPublish {
    param([switch]$AllowStop)

    $existing = Get-Process VMBLauncher -ErrorAction SilentlyContinue
    if ($existing) {
        if (-not $AllowStop) {
            $held = ($existing | ForEach-Object { "PID $($_.Id)" }) -join ', '
            throw "VMBLauncher is running ($held). Close it before publishing, or explicitly pass -ForceStopLauncher."
        }
        foreach ($p in $existing) {
            Write-Host "Stopping held VMBLauncher.exe (PID $($p.Id), started $($p.StartTime))..." -ForegroundColor Yellow
            Stop-Process -Id $p.Id -Force
        }
        $deadline = (Get-Date).AddSeconds(10)
        while ((Get-Date) -lt $deadline -and (Get-Process VMBLauncher -ErrorAction SilentlyContinue)) {
            Start-Sleep -Milliseconds 100
        }
    }
}

Write-Host "Running tests..." -ForegroundColor Cyan
& "$root\test.ps1"

Write-Host "Publishing VMBLauncher (self-contained, single-file, win-x64)..." -ForegroundColor Cyan
Push-Location $root
try {
    Prepare-LauncherPublish -AllowStop:$ForceStopLauncher
    dotnet publish -c Release -r win-x64 --self-contained true `
        /p:PublishSingleFile=true `
        /p:IncludeNativeLibrariesForSelfExtract=true `
        /p:EnableCompressionInSingleFile=true `
        /p:DebugType=embedded
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with code $LASTEXITCODE" }
} finally {
    Pop-Location
}

$out = Join-Path $root 'bin\Release\net9.0-windows\win-x64\publish'
$exe = Join-Path $out 'VMBLauncher.exe'

if (-not (Test-Path $exe)) {
    throw "Expected $exe but it wasn't produced."
}

$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "OK -- $exe ($size MB)" -ForegroundColor Green

# End-to-end headless smoke test against the freshly-built release binary. This
# suite is contractually noninteractive and nonmutating. GUI and real
# build/deploy action tests live in explicit opt-in suites and are never called
# from publish.ps1.
$smoke = Join-Path $root 'tests\headless_smoke.ps1'
if (Test-Path $smoke) {
    Write-Host "Running headless smoke against published binary..." -ForegroundColor Cyan
    & $smoke -Exe $exe
    if ($LASTEXITCODE -ne 0) { throw "Headless smoke failed (exit $LASTEXITCODE)" }
}

if ($OpenOutput) {
    Start-Process explorer.exe "/select,`"$exe`""
}
