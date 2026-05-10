# publish.ps1 -- builds VMBLauncher.exe as a self-contained, single-file Windows binary.
# Output: bin\Release\net9.0-windows\win-x64\publish\VMBLauncher.exe
#
# This is the file you ship to friends. No .NET install required on their machine.
# ~70 MB self-extracting exe; ~30-40 MB after compression. Cold-start ~1-2s.
#
# Usage:
#   .\publish.ps1                # build + opens the output folder
#   .\publish.ps1 -SkipOpen      # build only

param(
    [switch]$SkipOpen
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "Running tests..." -ForegroundColor Cyan
& "$root\test.ps1"

Write-Host "Publishing VMBLauncher (self-contained, single-file, win-x64)..." -ForegroundColor Cyan
Push-Location $root
try {
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

if (-not $SkipOpen) {
    Start-Process explorer.exe "/select,`"$exe`""
}
