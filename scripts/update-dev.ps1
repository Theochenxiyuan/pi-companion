[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$InstallExplorerIntegration
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$git = Get-Command git -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $git) {
    throw 'Git is required. Run scripts/bootstrap.ps1 -InstallMissing first.'
}

Push-Location $repositoryRoot
try {
    $insideWorkTree = (& $git.Source rev-parse --is-inside-work-tree 2>$null | Select-Object -First 1)
    if ($insideWorkTree -ne 'true') {
        throw 'The script must run from a Git clone of Pi Companion.'
    }

    $changes = @(& $git.Source status --porcelain=v1 --untracked-files=all)
    if ($changes.Count -gt 0) {
        throw "The working tree has $($changes.Count) uncommitted change(s). Commit, stash, or remove them before updating; this script will not overwrite local work."
    }

    $branch = (& $git.Source branch --show-current | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace([string]$branch)) {
        throw 'The repository is in detached-HEAD state. Switch back to main before updating.'
    }

    $upstream = (& $git.Source rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>$null | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace([string]$upstream)) {
        throw "Branch '$branch' has no upstream. For the tester clone, run: git branch --set-upstream-to=origin/main main"
    }

    Write-Host "Updating $branch from $upstream with fast-forward only..." -ForegroundColor Cyan
    & $git.Source pull --ff-only
    if ($LASTEXITCODE -ne 0) {
        throw "git pull --ff-only failed with exit code $LASTEXITCODE. No local changes were discarded."
    }

    & (Join-Path $PSScriptRoot 'doctor.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw 'The updated source requires missing or changed prerequisites. Run scripts/bootstrap.ps1 -InstallMissing, then retry.'
    }

    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "Pi Companion $Configuration build failed with exit code $LASTEXITCODE."
    }

    if ($InstallExplorerIntegration) {
        & (Join-Path $PSScriptRoot 'install-explorer-integration.ps1') `
            -Configuration $Configuration `
            -NoBuild
        if ($LASTEXITCODE -ne 0) {
            throw "Explorer integration installation failed with exit code $LASTEXITCODE."
        }
    }

    $head = (& $git.Source rev-parse --short HEAD | Select-Object -First 1)
    Write-Host ''
    Write-Host "Pi Companion is updated and verified at $head." -ForegroundColor Green
    Write-Host 'Start it with: .\scripts\run.ps1 -NoBuild'
}
finally {
    Pop-Location
}
