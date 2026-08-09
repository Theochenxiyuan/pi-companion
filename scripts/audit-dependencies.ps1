[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$chatDirectory = Join-Path $repositoryRoot 'src\PiCompanion.Chat'
$webSearchExtensionDirectory = Join-Path $repositoryRoot 'src\PiCompanion.WebSearchExtension'
$solutionPath = Join-Path $repositoryRoot 'PiCompanion.sln'
$localDotnet = Join-Path $env:LOCALAPPDATA 'PiCompanionTools\dotnet\dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet).Source }

foreach ($directory in @($chatDirectory, $webSearchExtensionDirectory)) {
    Push-Location $directory
    try {
        & npm audit --audit-level=moderate
        if ($LASTEXITCODE -ne 0) {
            throw "npm audit failed in $directory with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

$nugetOutput = @(& $dotnetCommand list $solutionPath package --vulnerable --include-transitive --format json 2>&1)
if ($LASTEXITCODE -ne 0) {
    $nugetOutput | Out-Host
    throw "NuGet vulnerability audit failed with exit code $LASTEXITCODE."
}

try {
    $nugetReport = ($nugetOutput -join [Environment]::NewLine) | ConvertFrom-Json
}
catch {
    $nugetOutput | Out-Host
    throw 'NuGet vulnerability audit did not return valid JSON.'
}

function Count-VulnerabilityEntries {
    param([AllowNull()] [object]$Value)

    if ($null -eq $Value -or $Value -is [string]) {
        return 0
    }

    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [pscustomobject]) {
        $count = 0
        foreach ($item in $Value) {
            $count += Count-VulnerabilityEntries $item
        }
        return $count
    }

    $count = 0
    foreach ($property in $Value.PSObject.Properties) {
        if ($property.Name -eq 'vulnerabilities') {
            $count += @($property.Value).Count
            continue
        }
        $count += Count-VulnerabilityEntries $property.Value
    }
    return $count
}

$nugetVulnerabilityCount = Count-VulnerabilityEntries $nugetReport
if ($nugetVulnerabilityCount -gt 0) {
    $nugetOutput | Out-Host
    throw "NuGet audit found $nugetVulnerabilityCount vulnerable package entr$(if ($nugetVulnerabilityCount -eq 1) { 'y' } else { 'ies' })."
}

Write-Host 'Dependency audit completed: no npm vulnerabilities at moderate-or-higher severity and no vulnerable NuGet packages.'
