# test.ps1 -- run the xUnit test suite headlessly.
# Returns 0 on success, non-zero on test failures.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root
try {
    & "$root\tests\check_noninteractive_contract.ps1"
    if ($LASTEXITCODE -ne 0) { throw "Noninteractive contract check failed (exit code $LASTEXITCODE)" }

    dotnet test "$root\tests\VmbLauncher.Tests.csproj" --nologo
    if ($LASTEXITCODE -ne 0) { throw "Tests failed (exit code $LASTEXITCODE)" }
} finally {
    Pop-Location
}
