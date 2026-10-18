# Licensed under the MIT license. See LICENSE.
#requires -Version 7.2
[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'secure-service-common.ps1')
Assert-MwbAdministrator
Assert-MwbOwnedService
if (Test-Path -LiteralPath $MwbInstallDirectory) { Assert-MwbProtectedObject $MwbInstallDirectory }
$service = Get-Service -Name $MwbServiceName -ErrorAction SilentlyContinue
if ($null -ne $service) {
    if ($service.Status -ne 'Stopped') { Stop-Service -Name $MwbServiceName; $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20)) }
    & (Join-Path ([Environment]::GetFolderPath('System')) 'sc.exe') delete $MwbServiceName
    if ($LASTEXITCODE -ne 0) { throw 'The optional service could not be removed.' }
}
Get-NetFirewallRule -Name $MwbFirewallName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
# Retain the protected binary files and private profile. Removing a registration
# must not turn a stale service configuration or a reused directory into a delete primitive.
Write-Output 'Optional service and its private-LAN firewall rule removed. Protected files/profile retained; the portable application is unchanged.'
