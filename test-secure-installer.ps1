# Licensed under the MIT license. See LICENSE.
# Negative tests for the privileged installation boundary; no system changes.
#requires -Version 7.2
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'secure-service-common.ps1')
$testDirectory=Join-Path $PSScriptRoot ('artifacts\installer-tests\'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
$profileFile=Join-Path $testDirectory 'test-profile.json'
$passed=0

function New-TestProfile {
    [ordered]@{schemaVersion=1;securityKey='Test-only-random-looking-key-123';localMachineName=[Environment]::MachineName;machineId=123;machineMatrix=@([Environment]::MachineName,'PEER-TEST','','');matrixOneRow=$true;tcpPort=15100;ipMappings='';wrapMouse=$false;easyMouse=1;moveMouseRelatively=$false;validateRemoteMachineIP=$false;sameSubnetOnly=$false}
}
function Test-Profile([string] $Name, [scriptblock] $Mutate, [bool] $Accepted=$false) {
    $profile=New-TestProfile
    & $Mutate $profile
    $json=$profile | ConvertTo-Json -Depth 5 -Compress
    [IO.File]::WriteAllText($profileFile,$json,[Text.UTF8Encoding]::new($false))
    $success=$true
    try { $null=Read-MwbSecureProfile $profileFile } catch { $success=$false }
    if ($success -ne $Accepted) { throw ('Installer profile test failed: '+$Name) }
    $script:passed++
}

foreach($scriptName in @('secure-service-common.ps1','install-secure-service.ps1','remove-secure-service.ps1','build-secure-preview.ps1')) {
    $tokens=$null; $errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $scriptName),[ref]$tokens,[ref]$errors)
    if ($errors.Count) { throw ('PowerShell parse errors in '+$scriptName) }
    if ($scriptName -eq 'secure-service-common.ps1') {
        $native=$ast.FindAll({param($node) $node -is [Management.Automation.Language.StringConstantExpressionAst] -and $node.Value.Contains('public static class MwbDirectoryNative')},$true)
        if (@($native).Count -ne 1) { throw 'Atomic directory helper is missing.' }
        if (-not ('MwbDirectoryNative' -as [type])) { Add-Type -TypeDefinition $native[0].Value }
    }
    $passed++
}
foreach($functionName in @('Assert-MwbProtectedTree','Assert-MwbTrustedAncestors','Read-MwbSecureProfile','New-MwbProtectedDirectory')) {
    $null=Get-Command $functionName -ErrorAction Stop
    $passed++
}

Test-Profile 'valid profile' {param($p)} $true
Test-Profile 'shift switching supported' {param($p) $p.easyMouse=3} $true
Test-Profile 'unicode peer hostname preserved' {param($p) $p.machineMatrix[1]='Teszt-Gép'} $true
Test-Profile 'original four-character key policy preserved' {param($p) $p.securityKey='abcd'} $true
Test-Profile 'unknown command field refused' {param($p) $p['command']='cmd.exe'}
Test-Profile 'wrong schema refused' {param($p) $p.schemaVersion=2}
Test-Profile 'missing schema refused' {param($p) $p.Remove('schemaVersion')}
Test-Profile 'zero identity refused' {param($p) $p.machineId=0}
Test-Profile 'overflow input port refused' {param($p) $p.tcpPort=65535}
Test-Profile 'negative port refused' {param($p) $p.tcpPort=-1}
Test-Profile 'unknown mouse mode refused' {param($p) $p.easyMouse=4}
Test-Profile 'wrong computer refused' {param($p) $p.localMachineName='NOT-THIS-COMPUTER'}
Test-Profile 'control in key refused' {param($p) $p.securityKey="test`nkey"}
Test-Profile 'short key refused' {param($p) $p.securityKey='abc'}
Test-Profile 'missing local layout slot refused' {param($p) $p.machineMatrix=@('PEER-TEST','','','')}
Test-Profile 'duplicate peer names refused' {param($p) $p.machineMatrix[2]='peer-test'}
Test-Profile 'invalid peer path refused' {param($p) $p.machineMatrix[1]='..\outside'}
Test-Profile 'invalid peer whitespace refused' {param($p) $p.machineMatrix[1]='A B'}
Test-Profile 'wrong layout cardinality refused' {param($p) $p.machineMatrix=@([Environment]::MachineName,'PEER-TEST')}
Test-Profile 'string boolean refused' {param($p) $p.wrapMouse='false'}
Test-Profile 'oversized mappings refused' {param($p) $p.ipMappings='X'*8193}
Test-Profile 'control in mappings refused' {param($p) $p.ipMappings="A$([char]1)B"}
Test-Profile 'tab and newline mappings supported' {param($p) $p.ipMappings="PEER-TEST`t192.0.2.2`r`n"} $true

$json=(New-TestProfile | ConvertTo-Json -Depth 5 -Compress).Replace('{"schemaVersion":1,','{"schemaVersion":1,"schemaVersion":1,')
[IO.File]::WriteAllText($profileFile,$json)
$rejected=$false
try {$null=Read-MwbSecureProfile $profileFile} catch {$rejected=$true}
if(-not $rejected){throw 'Duplicate JSON property accepted.'};$passed++
[IO.File]::WriteAllText($profileFile,(' '*65537))
$rejected=$false
try {$null=Read-MwbSecureProfile $profileFile} catch {$rejected=$true}
if(-not $rejected){throw 'Oversized profile accepted.'};$passed++

# A synthetic package exercises ValidateOnly without ever running its fake EXEs,
# requesting administrator access, opening ports or creating a Windows service.
$fixture=Join-Path $testDirectory 'package'
$payload=Join-Path $fixture 'service-payload'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
$portableFixture=Join-Path $fixture 'portable'
New-Item -ItemType Directory -Path $portableFixture -Force | Out-Null
foreach($name in @('secure-service-common.ps1','install-secure-service.ps1')) {Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $fixture}
$manifest=@()
foreach($name in @('MouseWithoutBorders.exe','MouseWithoutBorders.SecureDesktop.Service.exe')) {
    $source=if($name -ceq 'MouseWithoutBorders.exe'){Join-Path $portableFixture $name}else{Join-Path $payload $name}
    [IO.File]::WriteAllText($source,'Synthetic test data, never executed.')
    $manifest+=@{path=$name;sha256=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash}
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'service-files.json') -Encoding utf8NoBOM
(New-TestProfile | ConvertTo-Json -Depth 5) | Set-Content -LiteralPath $profileFile -Encoding utf8NoBOM
$null=& (Join-Path $fixture 'install-secure-service.ps1') -ProfilePath $profileFile -ValidateOnly
$passed++
[IO.File]::AppendAllText((Join-Path $portableFixture 'MouseWithoutBorders.exe'),'tampered')
$rejected=$false
try {$null=& (Join-Path $fixture 'install-secure-service.ps1') -ProfilePath $profileFile -ValidateOnly} catch {$rejected=$true}
if(-not $rejected){throw 'Tampered package accepted.'};$passed++
$manifest[0].path='../outside.exe'
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'service-files.json') -Encoding utf8NoBOM
$rejected=$false
try {$null=& (Join-Path $fixture 'install-secure-service.ps1') -ProfilePath $profileFile -ValidateOnly} catch {$rejected=$true}
if(-not $rejected){throw 'Package path traversal accepted.'};$passed++
Write-Output ('Installer boundary checks passed: '+$passed+'. No Windows service, firewall or protected directory was changed.')
