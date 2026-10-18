# Licensed under the MIT license. See LICENSE.
# Local optional-service packaging and installation helpers.
#requires -Version 7.2
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$MwbServiceName = 'MouseWithoutBorders.SecureDesktop'
$MwbInstallDirectory = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Mouse Without Borders Secure Desktop'
$MwbServiceExecutable = Join-Path $MwbInstallDirectory 'MouseWithoutBorders.SecureDesktop.Service.exe'
$MwbProfileParent = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'MouseWithoutBorders'
$MwbProfileDirectory = Join-Path $MwbProfileParent 'SecureDesktop'
$MwbProfileFile = Join-Path $MwbProfileDirectory 'MouseWithoutBorders.secure.json'
$MwbFirewallName = 'MouseWithoutBorders.SecureDesktop.Input'
$MwbTrustedSids = @('S-1-5-18', 'S-1-5-32-544')
$MwbTrustedInstallerSid = 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'
$MwbWriteRights = [Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
$MwbWriteMask = [long]$MwbWriteRights -bor 0x50000000L

function Assert-MwbAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Open PowerShell as administrator to install or remove the optional secure-desktop service.'
    }
    if (-not [Environment]::Is64BitProcess) { throw 'Use 64-bit PowerShell.' }
}

function Assert-MwbNoReparse([string] $Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if (((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'A service path contains a reparse point; installation was refused.'
            }
        }
        $parent = [IO.Directory]::GetParent($cursor)
        $cursor = if ($null -eq $parent) { $null } else { $parent.FullName }
    }
}

function Assert-MwbProtectedObject([string] $Path, [switch] $Private) {
    Assert-MwbNoReparse $Path
    $acl = Get-Acl -LiteralPath $Path
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($owner -notin $MwbTrustedSids) { throw 'An existing service object is not owned by Administrators or SYSTEM.' }
    $raw = [Security.AccessControl.RawSecurityDescriptor]::new($acl.GetSecurityDescriptorBinaryForm(), 0)
    if ($null -eq $raw.DiscretionaryAcl) { throw 'A service object has an unrestricted DACL.' }
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne 'Allow' -or ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly)) { continue }
        if ($rule.IdentityReference.Value -in $MwbTrustedSids) { continue }
        $unsafe = $MwbWriteMask
        if ($Private) { $unsafe = $unsafe -bor [long][Security.AccessControl.FileSystemRights]::ReadData -bor 0x80000000L }
        if (([long]$rule.FileSystemRights -band $unsafe) -ne 0) { throw 'An existing service object grants untrusted access.' }
    }
}

function New-MwbProtectedDirectory([string] $Path, [switch] $Private) {
    Assert-MwbNoReparse $Path
    if (Test-Path -LiteralPath $Path) { Assert-MwbProtectedObject $Path -Private:$Private; return }
    if (-not ('MwbDirectoryNative' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class MwbDirectoryNative {
    [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint version, out IntPtr descriptor, IntPtr size);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool CreateDirectory(string path, ref SecurityAttributes security);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr value);
    public static void Create(string path, string sddl) {
        IntPtr descriptor;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out descriptor, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            var attributes = new SecurityAttributes { Length=Marshal.SizeOf<SecurityAttributes>(), Descriptor=descriptor };
            if (!CreateDirectory(path, ref attributes)) throw new Win32Exception(Marshal.GetLastWin32Error());
        } finally { LocalFree(descriptor); }
    }
}
'@
    }
    $sddl = 'O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)'
    if (-not $Private) { $sddl += '(A;OICI;GRGX;;;BU)' }
    [MwbDirectoryNative]::Create([IO.Path]::GetFullPath($Path), $sddl)
    Assert-MwbProtectedObject $Path -Private:$Private
}

function Assert-MwbProtectedTree([string] $Root) {
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push([IO.Path]::GetFullPath($Root))
    $count = 0
    while ($pending.Count) {
        $directory = $pending.Pop()
        Assert-MwbProtectedObject $directory
        foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
            if (++$count -gt 4096) { throw 'The service installation contains too many files.' }
            Assert-MwbProtectedObject $entry.FullName
            if ($entry.PSIsContainer) { $pending.Push($entry.FullName) }
        }
    }
}

function Assert-MwbTrustedAncestors([string] $Path) {
    $cursor = [IO.DirectoryInfo]::new([IO.Path]::GetFullPath($Path)).Parent
    while ($null -ne $cursor) {
        if (Test-Path -LiteralPath $cursor.FullName) {
            Assert-MwbNoReparse $cursor.FullName
            $acl = Get-Acl -LiteralPath $cursor.FullName
            $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
            if ($owner -notin ($MwbTrustedSids + $MwbTrustedInstallerSid)) { throw 'A service ancestor has an untrusted owner.' }
            $raw = [Security.AccessControl.RawSecurityDescriptor]::new($acl.GetSecurityDescriptorBinaryForm(),0)
            if ($null -eq $raw.DiscretionaryAcl) { throw 'A service ancestor has an unrestricted DACL.' }
            foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
                if ($rule.AccessControlType -ne 'Allow' -or ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly)) { continue }
                if ($rule.IdentityReference.Value -in ($MwbTrustedSids + $MwbTrustedInstallerSid)) { continue }
                # Creating unrelated children at ProgramData/root is acceptable;
                # replacing this protected child or changing its ACL is not.
                $unsafe = [long]([Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership) -bor 0x50000000L
                if (([long]$rule.FileSystemRights -band $unsafe) -ne 0) { throw 'A service ancestor grants unsafe replacement access.' }
            }
        }
        $cursor = $cursor.Parent
    }
}

function Set-MwbFileSecurity([string] $Path, [switch] $Private) {
    Assert-MwbNoReparse $Path
    $sddl = 'O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)'
    if (-not $Private) { $sddl += '(A;;GRGX;;;BU)' }
    $acl = [Security.AccessControl.FileSecurity]::new()
    $acl.SetSecurityDescriptorSddlForm($sddl)
    Set-Acl -LiteralPath $Path -AclObject $acl
    Assert-MwbProtectedObject $Path -Private:$Private
}

function Read-MwbSecureProfile([string] $Path) {
    Assert-MwbNoReparse $Path
    $stream = [IO.File]::Open([IO.Path]::GetFullPath($Path),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        if ($stream.Length -eq 0 -or $stream.Length -gt 65536) { throw 'Secure profile must be between 1 and 65536 bytes.' }
        $bytes = [byte[]]::new([int]$stream.Length)
        $offset=0
        while ($offset -lt $bytes.Length) { $read=$stream.Read($bytes,$offset,$bytes.Length-$offset); if ($read -eq 0) { throw 'Secure profile was truncated.' }; $offset+=$read }
    } finally { $stream.Dispose() }
    $document = [Text.Json.JsonDocument]::Parse([Text.Encoding]::UTF8.GetString($bytes))
    try {
        $root = $document.RootElement
        if ($root.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw 'Secure profile must be a JSON object.' }
        $required = @('schemaVersion','securityKey','localMachineName','machineId','machineMatrix','matrixOneRow','tcpPort','ipMappings','wrapMouse','easyMouse','moveMouseRelatively','validateRemoteMachineIP','sameSubnetOnly')
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $root.EnumerateObject()) {
            if ($property.Name -cnotin $required -or -not $seen.Add($property.Name)) { throw 'Secure profile contains an unknown or duplicate property.' }
        }
        if ($seen.Count -ne $required.Count) { throw 'Secure profile is missing a required property.' }
        foreach ($name in @('schemaVersion','machineId','tcpPort','easyMouse')) {
            $number = 0
            if (-not $root.GetProperty($name).TryGetInt32([ref]$number)) { throw 'Secure profile has an invalid integer.' }
        }
        if ($root.GetProperty('schemaVersion').GetInt32() -ne 1) { throw 'Unsupported secure profile version.' }
        if ($root.GetProperty('machineId').GetInt32() -le 0) { throw 'A configured positive machine ID is required.' }
        if ($root.GetProperty('tcpPort').GetInt32() -lt 1 -or $root.GetProperty('tcpPort').GetInt32() -gt 65534) { throw 'Invalid TCP port.' }
        if ($root.GetProperty('easyMouse').GetInt32() -notin 0,1,2,3) { throw 'Invalid mouse-switching mode.' }
        foreach ($name in @('matrixOneRow','wrapMouse','moveMouseRelatively','validateRemoteMachineIP','sameSubnetOnly')) {
            if ($root.GetProperty($name).ValueKind -notin [Text.Json.JsonValueKind]::True,[Text.Json.JsonValueKind]::False) { throw 'Secure profile has an invalid boolean.' }
        }
        foreach ($name in @('securityKey','localMachineName','ipMappings')) {
            if ($root.GetProperty($name).ValueKind -ne [Text.Json.JsonValueKind]::String) { throw 'Secure profile has an invalid string.' }
        }
        $secret = $root.GetProperty('securityKey').GetString()
        if ($secret.Length -lt 4 -or $secret.Length -gt 1024 -or $secret -match '\p{Cc}') { throw 'Invalid shared key.' }
        $localName = $root.GetProperty('localMachineName').GetString()
        if ($localName -ine [Environment]::MachineName) { throw 'Export this profile on the computer where the service will be installed.' }
        $matrix = $root.GetProperty('machineMatrix')
        if ($matrix.ValueKind -ne [Text.Json.JsonValueKind]::Array -or $matrix.GetArrayLength() -ne 4) { throw 'Secure profile requires four machine slots.' }
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($item in $matrix.EnumerateArray()) {
            if ($item.ValueKind -ne [Text.Json.JsonValueKind]::String) { throw 'Invalid machine name.' }
            $name = $item.GetString()
            if ($name.Length -gt 0 -and ($name.Length -gt 63 -or $name -match '[\s\p{Cc}\\/:*?"<>|,;]' -or -not $names.Add($name))) { throw 'Invalid or duplicate machine name.' }
        }
        if (-not $names.Contains($localName)) { throw 'Secure profile must contain this computer in the machine matrix.' }
        $mappings = $root.GetProperty('ipMappings').GetString()
        if ($mappings.Length -gt 8192 -or $mappings -match '[\p{Cc}-[\r\n\t]]') { throw 'IP mappings are invalid or too large.' }
        # Return normalized validated data; never emit the shared key to the console.
        return $root.GetRawText()
    } finally { $document.Dispose() }
}

function Assert-MwbOwnedService {
    $registration = Get-ItemProperty -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\' + $MwbServiceName) -ErrorAction SilentlyContinue
    if ($null -ne $registration -and $registration.ImagePath -cne ('"' + $MwbServiceExecutable + '"')) {
        throw 'The service name is already used by a different executable; no changes were made.'
    }
}
