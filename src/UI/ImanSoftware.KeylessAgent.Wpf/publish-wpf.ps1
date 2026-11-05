# ===================================================================
# Publishes the KeylessAgent WPF desktop app (guide Phase 11).
#
#   Output:  <repo root>\artifacts\wpf
#   Command: dotnet publish -c Release -r win-x64 --self-contained false
#
# Framework-dependent: the machine needs the .NET 10 Desktop Runtime.
# Playwright driver files (.playwright folder + Microsoft.Playwright.dll)
# are verified after the publish (Core depends on Microsoft.Playwright).
#
# This script does NOT push NuGet packages and does NOT run the CLI's
# publish.ps1.
# ===================================================================

param(
    [switch]$SkipVerification
)

$ErrorActionPreference = "Stop"

$solutionRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
$project      = Join-Path $PSScriptRoot "ImanSoftware.KeylessAgent.Wpf.csproj"
$output       = Join-Path $solutionRoot "artifacts\wpf"

$script:summary = @()

function Write-Step {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host $Message -ForegroundColor Yellow
}

function Write-Action {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host $Message -ForegroundColor Blue
}

function Add-Summary {
    param([Parameter(Mandatory = $true)][string]$Message)
    $script:summary += $Message
}

function Show-Summary {
    Write-Host ""
    Write-Host "===================== Summary =====================" -ForegroundColor Cyan
    if ($script:summary.Count -eq 0) {
        Write-Host " (nothing was recorded)" -ForegroundColor Cyan
    }
    foreach ($line in $script:summary) {
        Write-Host " - $line" -ForegroundColor Cyan
    }
    Write-Host "====================================================" -ForegroundColor Cyan
}

if (-not (Test-Path $project)) {
    Write-Host "Project not found: $project" -ForegroundColor Red
    exit 1
}

Write-Step "Publishing the KeylessAgent WPF app (Release, win-x64, framework-dependent)..."
Write-Action "dotnet publish `"$project`" -c Release -r win-x64 --self-contained false -o `"$output`""
dotnet publish $project -c Release -r win-x64 --self-contained false -o $output
if ($LASTEXITCODE -ne 0) {
    Write-Host "dotnet publish failed with exit code $LASTEXITCODE." -ForegroundColor Red
    Show-Summary
    exit $LASTEXITCODE
}
Add-Summary "Published to $output"

if ($SkipVerification) {
    Write-Step "Skipping output verification (-SkipVerification)."
    Show-Summary
    exit 0
}

Write-Step "Verifying the publish output..."
$exe = Join-Path $output "ImanSoftware.KeylessAgent.Wpf.exe"
if (Test-Path $exe) {
    Add-Summary "Executable present: ImanSoftware.KeylessAgent.Wpf.exe"
} else {
    Write-Host "MISSING: $exe" -ForegroundColor Red
    Add-Summary "FAILED: executable not found"
    Show-Summary
    exit 1
}

$playwrightDriver = Join-Path $output ".playwright"
$playwrightLibrary = Join-Path $output "Microsoft.Playwright.dll"
if ((Test-Path $playwrightDriver) -and (Test-Path $playwrightLibrary)) {
    Add-Summary "Playwright driver present: .playwright\ + Microsoft.Playwright.dll"
} else {
    Write-Host "MISSING: Playwright driver files (.playwright folder or Microsoft.Playwright.dll)." -ForegroundColor Red
    Write-Host "Browser automation would fail at runtime; check the Microsoft.Playwright package reference." -ForegroundColor Red
    Add-Summary "FAILED: Playwright driver files not found"
    Show-Summary
    exit 1
}

$desktopRuntime = (dotnet --list-runtimes | Select-String "Microsoft.WindowsDesktop.App 10\.").Count -gt 0
if ($desktopRuntime) {
    Add-Summary ".NET 10 Desktop Runtime detected on this machine"
} else {
    Write-Host "NOTE: no .NET 10 Desktop Runtime found on this machine - the app needs it to start (framework-dependent publish)." -ForegroundColor Yellow
    Add-Summary "NOTE: .NET 10 Desktop Runtime not found (required to run)"
}

Show-Summary
exit 0
