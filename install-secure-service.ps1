# Licensed under the MIT license. See LICENSE.
#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string] $ProfilePath,
    [switch] $StartService,
    [switch] $ValidateOnly
)
. (Join-Path $PSScriptRoot 'secure-service-common.ps1')

$profileJson = Read-MwbSecureProfile $ProfilePath
$secureDesktopPolicy = Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name 'PromptOnSecureDesktop' -ErrorAction SilentlyContinue
if ($null -ne $secureDesktopPolicy -and $secureDesktopPolicy -eq 0) {
    Write-Warning 'UAC secure desktop is disabled. This preview handles elevation prompts only when the Windows switch-to-secure-desktop policy is enabled (PromptOnSecureDesktop=1); ordinary-desktop elevated windows are not supported. This installer does not change UAC policy.'
}
$payload = Join-Path $PSScriptRoot 'service-payload'
$manifestPath = Join-Path $PSScriptRoot 'service-files.json'
$manifest = @(Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json)
function Get-MwbPackageSource([string] $Relative) {
    if ($Relative -ceq 'MouseWithoutBorders.exe') { return Join-Path $PSScriptRoot 'portable\MouseWithoutBorders.exe' }
    return Join-Path $payload $Relative
}
if ($manifest.Count -lt 2 -or $manifest.Count -gt 500) { throw 'Invalid service package manifest.' }
$paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest) {
    $relative = [string]$entry.path
    if ($relative -notmatch '^[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*$' -or $relative.Split('/') -contains '..' -or -not $paths.Add($relative)) { throw 'Invalid or duplicate package path.' }
    $source = Get-MwbPackageSource $relative
    Assert-MwbNoReparse $source
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ine $entry.sha256) { throw 'Service package checksum validation failed.' }
}
foreach ($required in @('MouseWithoutBorders.exe','MouseWithoutBorders.SecureDesktop.Service.exe')) {
    if (-not $paths.Contains($required)) { throw 'The service package is missing an executable.' }
}
if ($ValidateOnly) { Write-Output 'Service package and secure profile validation passed. No system settings changed.'; return }

Assert-MwbAdministrator
Assert-MwbOwnedService
Assert-MwbTrustedAncestors $MwbInstallDirectory
Assert-MwbTrustedAncestors $MwbProfileDirectory
New-MwbProtectedDirectory $MwbInstallDirectory
# Validate every pre-existing runtime DLL before any installed executable can be
# invoked. A managed self-check alone runs after the native loader has started.
Assert-MwbProtectedTree $MwbInstallDirectory
New-MwbProtectedDirectory $MwbProfileParent -Private
New-MwbProtectedDirectory $MwbProfileDirectory -Private
New-MwbProtectedDirectory (Join-Path $MwbProfileDirectory 'runtime') -Private

$existingService = Get-Service -Name $MwbServiceName -ErrorAction SilentlyContinue
if ($null -ne $existingService -and $existingService.Status -ne 'Stopped') {
    Stop-Service -Name $MwbServiceName
    $existingService.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
}

foreach ($entry in $manifest) {
    $destination = Join-Path $MwbInstallDirectory $entry.path
    Assert-MwbNoReparse $destination
    $relativeDirectories = $entry.path.Split('/')
    $cursor = $MwbInstallDirectory
    for ($index=0; $index -lt $relativeDirectories.Length-1; $index++) {
        $cursor = Join-Path $cursor $relativeDirectories[$index]
        New-MwbProtectedDirectory $cursor
    }
    if (Test-Path -LiteralPath $destination) { Assert-MwbProtectedObject $destination }
    Copy-Item -LiteralPath (Get-MwbPackageSource $entry.path) -Destination $destination -Force
    Set-MwbFileSecurity $destination
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ine $entry.sha256) { throw 'Installed service file checksum differs from the reviewed package.' }
}
if (Test-Path -LiteralPath $MwbProfileFile) { Assert-MwbProtectedObject $MwbProfileFile -Private }
[IO.File]::WriteAllText($MwbProfileFile, $profileJson, [Text.UTF8Encoding]::new($false))
Set-MwbFileSecurity $MwbProfileFile -Private
$installedManifest = Join-Path $MwbInstallDirectory 'service-files.json'
if (Test-Path -LiteralPath $installedManifest) { Assert-MwbProtectedObject $installedManifest }
Copy-Item -LiteralPath $manifestPath -Destination $installedManifest -Force
Set-MwbFileSecurity $installedManifest

Assert-MwbProtectedTree $MwbInstallDirectory
$validation = Start-Process -FilePath $MwbServiceExecutable -ArgumentList '--validate-installation' -WorkingDirectory $MwbInstallDirectory -WindowStyle Hidden -PassThru -ErrorAction Stop
try {
    [void]$validation.Handle
    # This is a Windows-subsystem executable: direct invocation can return before
    # it exits and leave LASTEXITCODE unset. Always wait for this exact process.
    if (-not $validation.WaitForExit(20000)) { throw 'Protected installation validation timed out; the service has not been started.' }
    if ($validation.ExitCode -ne 0) { throw 'Protected installation validation failed; the service has not been started.' }
} finally {
    if (-not $validation.HasExited) { $validation.Kill() }
    $validation.Dispose()
}
if ($null -eq $existingService) {
    New-Service -Name $MwbServiceName -DisplayName 'Mouse Without Borders Secure Desktop' -BinaryPathName ('"' + $MwbServiceExecutable + '"') -StartupType Automatic -Description 'Optional authenticated keyboard and mouse control on the Windows sign-in and UAC desktop.' | Out-Null
}
$profile = $profileJson | ConvertFrom-Json
Get-NetFirewallRule -Name $MwbFirewallName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -Name $MwbFirewallName -DisplayName 'Mouse Without Borders secure desktop input (private LAN)' -Direction Inbound -Program (Join-Path $MwbInstallDirectory 'MouseWithoutBorders.exe') -Protocol TCP -LocalPort ([int]$profile.tcpPort+1) -RemoteAddress LocalSubnet -Profile Private -Action Allow | Out-Null
if ($StartService) { Start-Service -Name $MwbServiceName }
Write-Output ('Optional service installed. State: ' + (Get-Service -Name $MwbServiceName).Status + '. The existing Garage/PowerToys services were not modified.')
