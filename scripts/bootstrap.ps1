[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$InstallMissing,
    [switch]$SkipPiRuntime
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$doctor = Join-Path $PSScriptRoot 'doctor.ps1'

if (!$InstallMissing) {
    & $doctor
    $doctorExitCode = $LASTEXITCODE
    if ($doctorExitCode -ne 0) {
        Write-Host ''
        Write-Host 'To install supported missing dependencies with WinGet, rerun:' -ForegroundColor Yellow
        Write-Host '  .\scripts\bootstrap.ps1 -InstallMissing'
    }
    exit $doctorExitCode
}

$snapshotJson = (& $doctor -Json | Out-String)
$snapshot = $snapshotJson | ConvertFrom-Json
$failedIds = @($snapshot.checks |
    Where-Object { $_.required -and $_.status -eq 'fail' } |
    ForEach-Object { [string]$_.id })

$unfixable = @($failedIds | Where-Object { $_ -in @('windows', 'architecture') })
if ($unfixable.Count -gt 0) {
    & $doctor
    throw 'This machine does not meet the supported operating-system baseline.'
}

$winget = Get-Command winget -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $winget) {
    throw 'WinGet is required for automatic setup. Install or update App Installer, or install the prerequisites from docs/TESTER-GUIDE.md manually.'
}

function Install-WingetPackage {
    param(
        [Parameter(Mandatory)] [string]$Id,
        [string]$Override = '',
        [switch]$Force
    )

    if (!$PSCmdlet.ShouldProcess($Id, 'Install supported development prerequisite with WinGet')) {
        return
    }

    $wingetArguments = @(
        'install',
        '--id', $Id,
        '--exact',
        '--source', 'winget',
        '--accept-package-agreements',
        '--accept-source-agreements'
    )
    if ($Force) {
        $wingetArguments += '--force'
    }
    if (![string]::IsNullOrWhiteSpace($Override)) {
        $wingetArguments += @('--override', $Override)
    }

    & $winget.Source @wingetArguments
    if ($LASTEXITCODE -ne 0) {
        throw "WinGet failed to install $Id with exit code $LASTEXITCODE."
    }
}

if ('git' -in $failedIds) {
    Install-WingetPackage 'Git.Git'
}

if ('node' -in $failedIds -or 'npm' -in $failedIds) {
    Install-WingetPackage 'OpenJS.NodeJS.LTS' -Force
}

if ('dotnet' -in $failedIds) {
    Install-WingetPackage 'Microsoft.DotNet.SDK.10'
}

if ('visual-studio' -in $failedIds) {
    Install-WingetPackage 'Microsoft.VisualStudio.2022.BuildTools' `
        -Override '--wait --passive --norestart --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended' `
        -Force
}

if ('webview2' -in $failedIds) {
    Install-WingetPackage 'Microsoft.EdgeWebView2Runtime'
}

$pathParts = @(
    [Environment]::GetEnvironmentVariable('Path', 'Machine'),
    [Environment]::GetEnvironmentVariable('Path', 'User'),
    $env:Path
) | Where-Object { ![string]::IsNullOrWhiteSpace($_) }
$env:Path = $pathParts -join ';'

if (!$SkipPiRuntime -and 'pi' -in $failedIds) {
    $package = Get-Content -Raw -Encoding UTF8 (Join-Path $repositoryRoot 'src\PiCompanion.WebSearchExtension\package.json') | ConvertFrom-Json
    $expectedPi = [string]$package.devDependencies.'@earendil-works/pi-coding-agent'
    $npm = Get-Command npm -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $npm) {
        throw 'Node.js was installed, but npm is not visible in this PowerShell process. Open a new PowerShell window and rerun this script.'
    }

    if ($PSCmdlet.ShouldProcess("@earendil-works/pi-coding-agent@$expectedPi", 'Install the supported Pi Runtime globally with npm')) {
        & $npm.Source install --global "@earendil-works/pi-coding-agent@$expectedPi"
        if ($LASTEXITCODE -ne 0) {
            throw "npm failed to install Pi Runtime $expectedPi with exit code $LASTEXITCODE."
        }
    }
}

if ($WhatIfPreference) {
    Write-Host 'WhatIf completed; no packages were installed.'
    exit 0
}

Write-Host ''
Write-Host 'Setup actions completed. Rechecking the environment...' -ForegroundColor Cyan
& $doctor
$doctorExitCode = $LASTEXITCODE
if ($doctorExitCode -ne 0) {
    Write-Host ''
    Write-Host 'Some prerequisites still need attention. If a package was just installed, open a new PowerShell window and rerun scripts/doctor.ps1.' -ForegroundColor Yellow
}

exit $doctorExitCode
