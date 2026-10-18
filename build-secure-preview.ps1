# Licensed under the MIT license. See LICENSE.
#requires -Version 7.2
[CmdletBinding()]
param([string] $Dotnet = 'dotnet', [switch] $NoTests, [string] $Destination)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = $PSScriptRoot
$destination = if ($Destination) { [IO.Path]::GetFullPath($Destination) } else { Join-Path $root 'artifacts\secure-desktop-preview' }
if (Test-Path -LiteralPath $destination) { throw 'Preview output already exists. Choose a clean checkout/output before publishing again.' }
& (Join-Path $root 'validate-icon.ps1')
$app = Join-Path $root 'App\MouseWithoutBorders.csproj'
$service = Join-Path $root 'SecureDesktopService\MouseWithoutBorders.SecureDesktop.Service.csproj'
$tests = Join-Path $root 'MouseWithoutBorders.UnitTests\MouseWithoutBorders.UnitTests.csproj'
foreach ($project in @($app,$service,$tests)) {
    & $Dotnet build $project -c Release '-p:Platform=x64' '-p:RuntimeIdentifiers=win-x64' --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
if (-not $NoTests) {
    & $Dotnet test $tests -c Release '-p:Platform=x64' '-p:RuntimeIdentifiers=win-x64' --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
}
$payload = Join-Path $destination 'service-payload'
$portable = Join-Path $destination 'portable'
& $Dotnet publish $service -c Release '-p:Platform=x64' '-p:RuntimeIdentifiers=win-x64' -r win-x64 --self-contained true '-p:PublishSingleFile=false' '-p:DebugType=None' '-p:DebugSymbols=false' -o $payload
if ($LASTEXITCODE -ne 0) { throw 'Service publish failed.' }
& $Dotnet publish $app -c Release '-p:Platform=x64' '-p:RuntimeIdentifiers=win-x64' -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:EnableCompressionInSingleFile=true' '-p:PublishTrimmed=false' '-p:DebugType=None' '-p:DebugSymbols=false' -o $portable
if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }
foreach ($notice in @('LICENSE','NOTICE.md')) { Copy-Item -LiteralPath (Join-Path $root $notice) -Destination $payload }
$manifest = @(Get-ChildItem -LiteralPath $payload -File -Recurse | ForEach-Object {
    [ordered]@{path=[IO.Path]::GetRelativePath($payload,$_.FullName).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
$manifest += [ordered]@{path='MouseWithoutBorders.exe';sha256=(Get-FileHash -LiteralPath (Join-Path $portable 'MouseWithoutBorders.exe') -Algorithm SHA256).Hash}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $destination 'service-files.json') -Encoding utf8NoBOM
foreach ($file in @('install-secure-service.ps1','remove-secure-service.ps1','secure-service-common.ps1','LICENSE','NOTICE.md')) { Copy-Item -LiteralPath (Join-Path $root $file) -Destination $destination }
Copy-Item -LiteralPath (Join-Path $root 'docs\SECURE_DESKTOP_PREVIEW.md') -Destination (Join-Path $destination 'README.md')
Copy-Item -LiteralPath (Join-Path $root 'docs\SECURE_DESKTOP_VALIDATION.md') -Destination $destination
Copy-Item -LiteralPath (Join-Path $root 'docs\SECURE_DESKTOP_PREVIEW.hu.md') -Destination (Join-Path $destination 'README.hu.md')
Write-Output ('Preview package: '+$destination)
