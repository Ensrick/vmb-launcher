# test.ps1 -- run the xUnit test suite headlessly.
# Returns 0 on success, non-zero on test failures.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root
try {
    dotnet test "$root\tests\VmbLauncher.Tests.csproj" --nologo
    if ($LASTEXITCODE -ne 0) { throw "Tests failed (exit code $LASTEXITCODE)" }
} finally {
    Pop-Location
}
