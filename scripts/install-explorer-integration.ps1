[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$developerPolicy = Get-ItemProperty `
    -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' `
    -ErrorAction SilentlyContinue
if ($developerPolicy.AllowDevelopmentWithoutDevLicense -ne 1 -and
    $developerPolicy.AllowAllTrustedApps -ne 1) {
    throw 'The unsigned sparse development package requires Windows Developer Mode or sideloading. This script does not change system security policy.'
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (!$NoBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Pi Companion build failed with exit code $LASTEXITCODE." }
}

$developmentPackages = Get-AppxPackage |
    Where-Object { $_.Name -eq 'PiCompanion.Development' }
foreach ($package in $developmentPackages) {
    Remove-AppxPackage -Package $package.PackageFullName
}

$stagingRoot = Join-Path $repositoryRoot 'artifacts\explorer-integration'
if (Test-Path -LiteralPath $stagingRoot) {
    $resolvedStagingRoot = [IO.Path]::GetFullPath($stagingRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $staleDirectories = Get-ChildItem -LiteralPath $resolvedStagingRoot -Directory |
        Where-Object { $_.Name.StartsWith("$Configuration-", [StringComparison]::OrdinalIgnoreCase) }
    foreach ($directory in $staleDirectories) {
        $resolvedDirectory = [IO.Path]::GetFullPath($directory.FullName)
        if (!$resolvedDirectory.StartsWith(
            "$resolvedStagingRoot$([IO.Path]::DirectorySeparatorChar)",
            [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove stale staging directory outside the Explorer integration root: $resolvedDirectory"
        }

        Remove-Item -LiteralPath $resolvedDirectory -Recurse -Force
    }
}

$stagingDirectory = (& (Join-Path $PSScriptRoot 'build-explorer-integration.ps1') `
        -Configuration $Configuration `
        -NoBuild | Select-Object -Last 1)
$manifestPath = Join-Path $stagingDirectory 'AppxManifest.xml'

Add-AppxPackage `
    -Register $manifestPath `
    -ExternalLocation $stagingDirectory `
    -ForceApplicationShutdown

Write-Host 'Pi Companion Explorer integration registered for the current user.'
Write-Host 'Restart File Explorer or sign out and back in before testing Ask Pi Companion.'
