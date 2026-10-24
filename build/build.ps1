#Requires -Version 5.1
<#
.SYNOPSIS
    Reproducible build for Orap Craft Launcher.

.DESCRIPTION
    Restores, builds, tests and publishes the launcher:
      * GUI  -> dist\win-x64\OrapCraftLauncher.exe        (self-contained, single file)
      * CLI  -> dist\win-x64\cli\orap.exe            (self-contained, single file)

    The script only touches this repository and a local artifact folder. It never modifies
    machine-wide configuration and never installs anything system-wide.

.PARAMETER Configuration
    Build configuration. Release by default.

.PARAMETER SkipTests
    Skip the unit test run.

.PARAMETER DotnetRoot
    Directory containing dotnet.exe. Use when the SDK is not on PATH.

.PARAMETER NoPublish
    Only build (and optionally test), skip publishing.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\build.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\build.ps1 -DotnetRoot D:\dotnet -SkipTests
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$NoPublish,
    [string]$DotnetRoot
)

$ErrorActionPreference = 'Stop'
$script:RepoRoot = Split-Path -Parent $PSScriptRoot
$script:Solution = Join-Path $script:RepoRoot 'OrionLauncher.sln'
$script:DistRoot = Join-Path $script:RepoRoot 'dist\win-x64'

function Write-Step([string]$Message) {
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Resolve-Dotnet {
    if ($DotnetRoot) {
        $candidate = Join-Path $DotnetRoot 'dotnet.exe'
        if (-not (Test-Path $candidate)) {
            throw "在 $DotnetRoot 下找不到 dotnet.exe。请用 -DotnetRoot 指定包含 dotnet.exe 的目录。"
        }
        return $candidate
    }

    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    foreach ($guess in @('D:\dotnet\dotnet.exe', "$env:ProgramFiles\dotnet\dotnet.exe")) {
        if ($guess -and (Test-Path $guess)) { return $guess }
    }

    throw '找不到 .NET SDK（dotnet.exe）。请安装 .NET 10 SDK，或用 -DotnetRoot 指定其目录。'
}

function Invoke-Dotnet {
    param([string[]]$Arguments)

    & $script:Dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw ("dotnet " + ($Arguments -join ' ') + " 失败 (退出代码 $LASTEXITCODE)")
    }
}

$script:Dotnet = Resolve-Dotnet
$sdkVersion = & $script:Dotnet --version
Write-Host "Orap Craft Launcher 构建" -ForegroundColor Green
Write-Host "  仓库:   $script:RepoRoot"
Write-Host "  SDK:    $sdkVersion ($script:Dotnet)"
Write-Host "  配置:   $Configuration"

$started = Get-Date

Write-Step '还原依赖'
Invoke-Dotnet @('restore', $script:Solution, '--nologo')

Write-Step "编译 ($Configuration)"
Invoke-Dotnet @('build', $script:Solution, '-c', $Configuration, '--no-restore', '--nologo', '-v', 'minimal')

if (-not $SkipTests) {
    Write-Step '运行单元测试'
    Invoke-Dotnet @('test', (Join-Path $script:RepoRoot 'tests\Orion.Tests\Orion.Tests.csproj'),
        '-c', $Configuration, '--no-build', '--nologo', '-v', 'minimal')
}

if (-not $NoPublish) {
    Write-Step '发布 GUI（self-contained 单文件）'
    if (Test-Path $script:DistRoot) { Remove-Item -Recurse -Force $script:DistRoot }

    Invoke-Dotnet @('publish', (Join-Path $script:RepoRoot 'src\Orion.App\Orion.App.csproj'),
        '-c', $Configuration, '-r', 'win-x64', '-p:PublishSingleFile=true', '-p:SelfContained=true',
        '--nologo', '-v', 'minimal', '-o', $script:DistRoot)

    Write-Step '发布 CLI（self-contained 单文件）'
    Invoke-Dotnet @('publish', (Join-Path $script:RepoRoot 'src\Orion.Cli\Orion.Cli.csproj'),
        '-c', $Configuration, '-r', 'win-x64', '-p:PublishSingleFile=true', '-p:SelfContained=true',
        '--nologo', '-v', 'minimal', '-o', (Join-Path $script:DistRoot 'cli'))

    # Debug symbols are published next to the executable; keep the release folder tidy.
    Get-ChildItem $script:DistRoot -Recurse -Filter '*.pdb' | Remove-Item -Force -ErrorAction SilentlyContinue

    Write-Step '发布产物'
    Get-ChildItem $script:DistRoot -Recurse -File |
        Select-Object @{n = 'File'; e = { $_.FullName.Substring($script:DistRoot.Length + 1) } },
                      @{n = 'Size(MB)'; e = { [math]::Round($_.Length / 1MB, 2) } } |
        Format-Table -AutoSize | Out-String | Write-Host
}

$elapsed = (Get-Date) - $started
Write-Host ''
Write-Host ("构建完成，用时 {0:0.0} 秒。" -f $elapsed.TotalSeconds) -ForegroundColor Green
if (-not $NoPublish) {
    Write-Host "直接运行: $script:DistRoot\OrapCraftLauncher.exe" -ForegroundColor Green
    Write-Host "命令行:   $script:DistRoot\cli\orap.exe help" -ForegroundColor Green
}
