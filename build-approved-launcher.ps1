# Builds the optional native bootstrap; does not install or execute it.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$source = Join-Path $PSScriptRoot 'ApprovedLaunchNative\launcher.c'
$outputDirectory = Join-Path $PSScriptRoot 'artifacts\approved-launch-native'
$output = Join-Path $outputDirectory 'MwbApprovedLauncher.exe'

function Assert-NoReparseAncestors([string] $Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and
            ([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Native build paths must not contain symbolic links or junctions.'
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}

if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw 'The approved native launcher source is missing.' }
Assert-NoReparseAncestors $source
Assert-NoReparseAncestors $outputDirectory
[void][IO.Directory]::CreateDirectory($outputDirectory)
$compilerPath = $null
$compilerKind = $null

if ($env:MWB_ZIG_EXE) {
    if (-not [IO.Path]::IsPathFullyQualified($env:MWB_ZIG_EXE) -or
        -not (Test-Path -LiteralPath $env:MWB_ZIG_EXE -PathType Leaf) -or
        [IO.Path]::GetFileName($env:MWB_ZIG_EXE) -ine 'zig.exe') {
        throw 'MWB_ZIG_EXE must identify an existing absolute zig.exe path.'
    }
    $compilerPath = [IO.Path]::GetFullPath($env:MWB_ZIG_EXE)
    $compilerKind = 'Zig C / x86_64-windows-gnu'
    $savedLocalCache = $env:ZIG_LOCAL_CACHE_DIR
    $savedGlobalCache = $env:ZIG_GLOBAL_CACHE_DIR
    try {
        $env:ZIG_LOCAL_CACHE_DIR = Join-Path $outputDirectory 'cache'
        $env:ZIG_GLOBAL_CACHE_DIR = Join-Path $outputDirectory 'global-cache'
        Assert-NoReparseAncestors $env:ZIG_LOCAL_CACHE_DIR
        Assert-NoReparseAncestors $env:ZIG_GLOBAL_CACHE_DIR
        $arguments = @('cc', '-target', 'x86_64-windows-gnu', '-std=c11', '-O2', '-Wall', '-Wextra', '-Werror',
            '-municode', '-Wl,--subsystem,windows', '-fstack-protector-strong', '-Wl,--dynamicbase', '-Wl,--nxcompat', '-Wl,--high-entropy-va',
            $source, '-o', $output, '-ladvapi32', '-luserenv', '-lshell32', '-lole32')
        & $compilerPath @arguments
        if ($LASTEXITCODE -ne 0) { throw "The approved native launcher build failed (Zig exit code $LASTEXITCODE)." }
    }
    finally {
        $env:ZIG_LOCAL_CACHE_DIR = $savedLocalCache
        $env:ZIG_GLOBAL_CACHE_DIR = $savedGlobalCache
    }
}
else {
    $msvc = Get-Command 'cl.exe' -CommandType Application -ErrorAction SilentlyContinue
    $arguments = @('/nologo', '/std:c11', '/O2', '/W4', '/WX', '/MT',
        "/Fo$(Join-Path $outputDirectory 'launcher.obj')", "/Fe$output", $source,
        '/link', '/SUBSYSTEM:WINDOWS', '/DYNAMICBASE', '/NXCOMPAT', '/HIGHENTROPYVA',
        'advapi32.lib', 'userenv.lib', 'shell32.lib', 'ole32.lib')
    if ($msvc) {
        $compilerPath = $msvc.Source
        $compilerKind = 'MSVC / developer environment'
        & $compilerPath @arguments
        if ($LASTEXITCODE -ne 0) { throw "The approved native launcher build failed (MSVC exit code $LASTEXITCODE)." }
    }
    else {
        $programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
        $vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
            throw 'No native C toolchain was found. Use Visual Studio C++ Build Tools, or set MWB_ZIG_EXE to a verified Zig installation.'
        }
        $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($LASTEXITCODE -ne 0 -or -not $installation -or @($installation).Count -ne 1) {
            throw 'Visual Studio C++ Build Tools were not found by vswhere.'
        }
        $developerCommand = Join-Path ([string]$installation).Trim() 'Common7\Tools\VsDevCmd.bat'
        if (-not (Test-Path -LiteralPath $developerCommand -PathType Leaf)) { throw 'VsDevCmd.bat is missing.' }
        foreach ($path in @($developerCommand, $source, $outputDirectory)) {
            if ($path.IndexOfAny([char[]]@('"', '%', '!', "`r", "`n")) -ge 0) {
                throw 'The native MSVC build path contains unsupported command-script characters.'
            }
        }
        # This generated build command accepts no application/user arguments.
        # /d disables Command Processor AutoRun; /v:off disables delayed expansion.
        $commandFile = Join-Path $outputDirectory 'build-msvc.cmd'
        $objectPath = Join-Path $outputDirectory 'launcher.obj'
        $command = @(
            '@echo off',
            ('call "' + $developerCommand + '" -arch=amd64 -host_arch=amd64'),
            'if errorlevel 1 exit /b %errorlevel%',
            ('cl.exe /nologo /std:c11 /O2 /W4 /WX /MT /Fo"' + $objectPath + '" /Fe"' + $output + '" "' + $source +
                '" /link /SUBSYSTEM:WINDOWS /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA advapi32.lib userenv.lib shell32.lib ole32.lib'),
            'exit /b %errorlevel%'
        ) -join "`r`n"
        [IO.File]::WriteAllText($commandFile, $command + "`r`n", [Text.ASCIIEncoding]::new())
        $commandProcessor = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)) 'System32\cmd.exe'
        & $commandProcessor /d /v:off /s /c ('"' + $commandFile + '"')
        if ($LASTEXITCODE -ne 0) { throw "The approved native launcher build failed (MSVC setup/build exit code $LASTEXITCODE)." }
        $compilerPath = $developerCommand
        $compilerKind = 'MSVC / vswhere + VsDevCmd'
    }
}

if (-not (Test-Path -LiteralPath $output -PathType Leaf)) { throw 'The native compiler did not produce the approved launcher.' }
$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
[IO.File]::WriteAllText($output + '.sha256', $hash + '  MwbApprovedLauncher.exe' + "`n", [Text.UTF8Encoding]::new($false))
$result = [ordered]@{
    Source = 'ApprovedLaunchNative/launcher.c'
    SourceSha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    Compiler = $compilerKind
    CompilerPath = $compilerPath
    Output = 'artifacts/approved-launch-native/MwbApprovedLauncher.exe'
    Bytes = (Get-Item -LiteralPath $output).Length
    Sha256 = $hash
}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputDirectory 'build-result.json') -Encoding utf8
Write-Host "Approved native launcher built: $output"
