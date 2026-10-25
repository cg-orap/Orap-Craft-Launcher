<#
.SYNOPSIS
    Pre-commit guardrail for the launcher: build, test, publish and prove the EXE actually starts.

.DESCRIPTION
    Every XAML or resource change can compile cleanly and still kill the app at startup (an
    unresolvable Icon="Assets/app.ico" did exactly that). This script turns that class of mistake
    into a failed command instead of a broken release.

    Stages, in order (each one aborts the script on failure):
      1. build    - dotnet build of the whole solution
      2. test     - xUnit suite
      3. publish  - self-contained single-file release into dist\win-x64
      4. smoke    - start the published EXE against a throwaway data root, require a window and
                    require that its log has no startup-failed entry

.PARAMETER DotnetRoot
    Folder that contains dotnet.exe (default: $env:DSH_DOTNET_ROOT, else D:\dotnet, else PATH).

.PARAMETER SkipPublish
    Reuse the existing dist build (stage 3 is skipped, stage 4 still runs).

.PARAMETER SkipSmoke
    Skip stage 4 (only when the machine cannot open windows, e.g. CI in a session 0 container).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File build\verify.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File build\verify.ps1 -SkipPublish
#>
[CmdletBinding()]
param(
    [string]$DotnetRoot = '',
    [switch]$SkipPublish,
    [switch]$SkipSmoke,
    [int]$SmokeTimeoutSeconds = 40
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repo = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repo 'OrionLauncher.sln'
$tests = Join-Path $repo 'tests\Orion.Tests\Orion.Tests.csproj'
$exe = Join-Path $repo 'dist\win-x64\OrapCraftLauncher.exe'
$smokeRoot = Join-Path $repo 'artifacts\verify-root'

function Write-Stage([string]$name) {
    Write-Host ''
    Write-Host "=== $name ===" -ForegroundColor Cyan
}

function Fail([string]$message) {
    Write-Host "FAILED: $message" -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------- dotnet location
if (-not $DotnetRoot) {
    if ($env:DSH_DOTNET_ROOT) { $DotnetRoot = $env:DSH_DOTNET_ROOT }
    elseif (Test-Path 'D:\dotnet\dotnet.exe') { $DotnetRoot = 'D:\dotnet' }
}

$dotnet = if ($DotnetRoot) { Join-Path $DotnetRoot 'dotnet.exe' } else { 'dotnet' }
if ($DotnetRoot -and -not (Test-Path $dotnet)) { Fail "dotnet.exe not found under $DotnetRoot" }
$env:DOTNET_ROOT = if ($DotnetRoot) { $DotnetRoot } else { $env:DOTNET_ROOT }
if ($DotnetRoot) { $env:PATH = "$DotnetRoot;$env:PATH" }

Write-Host "repo:   $repo"
Write-Host "dotnet: $dotnet"

# ---------------------------------------------------------------- 1. build
Write-Stage '1/4 build'
& $dotnet build $solution -v q --nologo
if ($LASTEXITCODE -ne 0) { Fail 'solution build failed' }

# ---------------------------------------------------------------- 2. test
Write-Stage '2/4 test'
& $dotnet test $tests -v q --nologo
if ($LASTEXITCODE -ne 0) { Fail 'test suite failed' }

# ---------------------------------------------------------------- 3. publish
if ($SkipPublish) {
    Write-Stage '3/4 publish (skipped)'
    if (-not (Test-Path $exe)) { Fail "-SkipPublish was given but $exe does not exist" }
}
else {
    Write-Stage '3/4 publish'
    # the publish step rewrites dist, so a running instance would lock the EXE
    Get-Process OrapCraftLauncher -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build.ps1') -DotnetRoot $DotnetRoot
    if ($LASTEXITCODE -ne 0) { Fail 'publish failed' }
    if (-not (Test-Path $exe)) { Fail "publish finished but $exe is missing" }
}

# ---------------------------------------------------------------- 4. startup smoke
if ($SkipSmoke) {
    Write-Stage '4/4 smoke (skipped)'
    Write-Host 'verify: build, tests and publish OK (startup not exercised)' -ForegroundColor Green
    exit 0
}

Write-Stage '4/4 startup smoke'
Get-Process OrapCraftLauncher -ErrorAction SilentlyContinue | Stop-Process -Force
if (Test-Path $smokeRoot) { Remove-Item $smokeRoot -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $smokeRoot | Out-Null

Start-Process -FilePath $exe -ArgumentList '--root', $smokeRoot
$deadline = (Get-Date).AddSeconds($SmokeTimeoutSeconds)
$window = $false

while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    $proc = Get-Process OrapCraftLauncher -ErrorAction SilentlyContinue
    if (-not $proc) { break }          # died: reported below with the log tail
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne 0) { $window = $true; break }
}

$proc = Get-Process OrapCraftLauncher -ErrorAction SilentlyContinue
$log = Get-ChildItem (Join-Path $smokeRoot 'logs') -Filter 'launcher-*.log' -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1

$startupError = $false
$ready = $false
if ($log) {
    # -Quiet already returns a boolean; comparing it to $null would always be true.
    $startupError = [bool](Select-String -Path $log.FullName -Pattern 'startup-failed' -SimpleMatch -Quiet)

    # A window can appear while the splash never finishes (that shipped once). Wait for the ready
    # marker the shell writes once IsReady is released.
    $readyDeadline = (Get-Date).AddSeconds($SmokeTimeoutSeconds)
    while ((Get-Date) -lt $readyDeadline) {
        $ready = [bool](Select-String -Path $log.FullName -Pattern '[ui] ready' -SimpleMatch -Quiet)
        if ($ready) { break }
        Start-Sleep -Milliseconds 500
    }
}

if ($proc) { $proc | Stop-Process -Force }
Start-Sleep -Milliseconds 500

if (-not $ready -and $proc -and $window -and -not $startupError) {
    if ($log) {
        Write-Host '--- log tail ---' -ForegroundColor Yellow
        Get-Content $log.FullName -Encoding utf8 -Tail 12 | ForEach-Object { Write-Host "  $_" }
    }
    if ($proc) { $proc | Stop-Process -Force }
    Fail 'the window appeared but initialization never reported ready (stuck splash)'
}

if (-not $proc -or -not $window -or $startupError) {
    if ($log) {
        Write-Host '--- log tail ---' -ForegroundColor Yellow
        Get-Content $log.FullName -Encoding utf8 -Tail 12 | ForEach-Object { Write-Host "  $_" }
    }
    else {
        Write-Host 'no launcher log was produced' -ForegroundColor Yellow
    }

    if ($startupError) { Fail 'the launcher logged startup-failed' }
    if (-not $proc)   { Fail 'the published EXE exited during startup' }
    Fail "no main window appeared within $SmokeTimeoutSeconds s"
}

Write-Host ''
Write-Host "verify: build, tests, publish and startup smoke all OK" -ForegroundColor Green
Write-Host "  exe:      $exe"
Write-Host "  data root: $smokeRoot"
exit 0
