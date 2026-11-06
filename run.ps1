param([switch]$SmokeTest, [switch]$Diagnostics)

$ErrorActionPreference = 'Stop'
$localDotnet = Join-Path $env:LOCALAPPDATA 'SWT\dotnet\dotnet.exe'
$dotnet = if (Test-Path $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_ROOT = Split-Path $dotnet
Push-Location $PSScriptRoot
try {
    [string[]]$appArgs = if ($SmokeTest) { @('--smoke-test') } else { @() }
    if ($Diagnostics) {
        $appArgs += '--diagnostics'
        Write-Host "Diagnostics: $env:LOCALAPPDATA\SWT\logs (no shell text or commands recorded)"
    }
    & $dotnet run --project .\src\SWT.Desktop -- @appArgs
    $result = $LASTEXITCODE
}
finally {
    Pop-Location
}
exit $result
