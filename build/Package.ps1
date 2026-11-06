[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$')]
    [string]$Version,
    [string]$IsccPath = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = 'Stop'
$numericVersion = ($Version -split '-', 2)[0]
foreach ($part in $numericVersion.Split('.')) {
    if ([long]$part -gt 65535) { throw "Version components must not exceed 65535." }
}
if (-not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw "Inno Setup compiler not found at '$IsccPath'. Install Inno Setup 6 or pass -IsccPath."
}

$root = Split-Path $PSScriptRoot
$output = Join-Path $root 'artifacts'
$staging = Join-Path ([System.IO.Path]::GetTempPath()) "SWT-package-$([guid]::NewGuid())"
$publish = Join-Path $staging 'publish'
$name = "SWT-$Version-win-x64"
New-Item -ItemType Directory -Path $output -Force | Out-Null
New-Item -ItemType Directory -Path $publish | Out-Null
Push-Location $root
try {
    dotnet publish .\src\SWT.Desktop\SWT.Desktop.csproj --configuration Release `
        --runtime win-x64 --self-contained true --output $publish `
        "-p:Version=$Version" "-p:FileVersion=$numericVersion.0" `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -p:ContinuousIntegrationBuild=true
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
    if (-not (Test-Path "$publish\SWT.Desktop.exe")) { throw "Published executable is missing." }

    Copy-Item .\LICENSE -Destination $publish
    Compress-Archive -Path "$publish\*" -DestinationPath "$output\$name.zip" -Force
    & $IsccPath "/DAppVersion=$Version" "/DNumericVersion=$numericVersion" `
        "/DPublishDir=$publish" "/DOutputDir=$output" .\build\SWT.iss
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed." }

    $files = @("$output\$name.zip", "$output\$name-setup.exe")
    $checksums = foreach ($file in $files) {
        $hash = Get-FileHash -LiteralPath $file -Algorithm SHA256
        "$($hash.Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($file))"
    }
    Set-Content -LiteralPath "$output\SHA256SUMS.txt" -Value $checksums -Encoding ascii
}
finally {
    Pop-Location
    Remove-Item -LiteralPath $staging -Recurse -Force
}
