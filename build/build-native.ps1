<#

.SYNOPSIS

    Compile the cubiomes native engine (OrapCubiomes.dll).



.DESCRIPTION

    cubiomes is a C library (MIT). This script looks for a compiler here:

      D:\tools\mingw64\bin\gcc.exe   (WinLibs, unzip and run)

      D:\tools\zig\zig.exe           (zig cc also compiles C)

    Output: src/Orion.Core/Native/OrapCubiomes.dll



    -fwrapv must stay: cubiomes relies on Java-style two\'s complement overflow.

#>

param(

    [string]$CubiomesDir = (Join-Path $PSScriptRoot '..\third-party\cubiomes'),

    [string]$OutDir = (Join-Path $PSScriptRoot '..\src\Orion.Core\Native'),

    [string]$GccPath = '',

    [string]$ZigPath = ''

)



$ErrorActionPreference = 'Stop'



function Find-Compiler {

    param([string]$Gcc, [string]$Zig)



    if ($Gcc -and (Test-Path $Gcc)) { return @{ Kind = 'gcc'; Path = $Gcc } }

    if ($Zig -and (Test-Path $Zig)) { return @{ Kind = 'zig'; Path = $Zig } }



    foreach ($candidate in @('D:\tools\mingw64\bin\gcc.exe', 'D:\tools\mingw64\bin\x86_64-w64-mingw32-gcc.exe')) {

        if (Test-Path $candidate) { return @{ Kind = 'gcc'; Path = $candidate } }

    }



    if (Test-Path 'D:\tools\zig\zig.exe') { return @{ Kind = 'zig'; Path = 'D:\tools\zig\zig.exe' } }



    $gcc = Get-Command gcc -ErrorAction SilentlyContinue

    if ($gcc) { return @{ Kind = 'gcc'; Path = $gcc.Source } }



    $zig = Get-Command zig -ErrorAction SilentlyContinue

    if ($zig) { return @{ Kind = 'zig'; Path = $zig.Source } }



    return $null

}



$compiler = Find-Compiler -Gcc $GccPath -Zig $ZigPath

if (-not $compiler) {

    throw 'No C compiler found. Unzip WinLibs gcc.exe to D:\tools\mingw64, or put zig.exe in D:\tools\zig.'

}



$sources = @(Get-ChildItem -Path $CubiomesDir -Filter '*.c' | ForEach-Object { $_.FullName })

$bridge = Join-Path $OutDir 'orap_cubiomes.c'

if (-not (Test-Path $bridge)) { throw "bridge not found: $bridge" }



New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$target = Join-Path $OutDir 'OrapCubiomes.dll'



$cliArgs = @()

if ($compiler.Kind -eq 'zig') { $cliArgs += 'cc' }

$cliArgs += @('-O2', '-fwrapv', '-static', '-static-libgcc', '-shared', '-o', $target, $bridge)

$cliArgs += $sources

$cliArgs += @('-I', $CubiomesDir, '-lm')



Write-Output ("==> compiler: {0} ({1} C files)" -f $compiler.Kind, ($sources.Count + 1))

& $compiler.Path @cliArgs

if ($LASTEXITCODE -ne 0) { throw "compile failed (exit $LASTEXITCODE)" }



$info = Get-Item $target

Write-Output ("==> built {0} ({1:N0} KB)" -f $info.Name, ($info.Length / 1KB))

