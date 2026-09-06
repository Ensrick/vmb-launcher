# test.ps1 -- run the xUnit test suite headlessly.
# Returns 0 on success, non-zero on test failures.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root
try {
    & "$root\tests\check_noninteractive_contract.ps1"
    if ($LASTEXITCODE -ne 0) { throw "Noninteractive contract check failed (exit code $LASTEXITCODE)" }

    dotnet test "$root\tests\VmbLauncher.Tests.csproj" -c Debug --nologo
    if ($LASTEXITCODE -ne 0) { throw "Tests failed (exit code $LASTEXITCODE)" }

    # The wrapper fixture uses a private test mutex; never run it against a
    # shipping binary or depend on a stale normal bin\Debug from a local build.
    $wrapperExe = Join-Path $root 'bin\TestHooks\Debug\net9.0-windows\VMBLauncher.exe'
    if (-not (Test-Path -LiteralPath $wrapperExe -PathType Leaf)) {
        throw "Test graph did not produce the wrapper fixture executable: $wrapperExe"
    }
    & "$root\tests\transaction_wrapper_smoke.ps1" -Exe $wrapperExe
    if ($LASTEXITCODE -ne 0) { throw "Transaction wrapper smoke failed (exit code $LASTEXITCODE)" }
} finally {
    Pop-Location
}
