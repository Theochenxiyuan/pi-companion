[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$chatDirectory = Join-Path $repositoryRoot 'src\PiCompanion.Chat'
$webSearchExtensionDirectory = Join-Path $repositoryRoot 'src\PiCompanion.WebSearchExtension'
$solutionPath = Join-Path $repositoryRoot 'PiCompanion.sln'
$explorerProject = Join-Path $repositoryRoot 'src\PiCompanion.ExplorerCommand\PiCompanion.ExplorerCommand.vcxproj'
$explorerSmokeProject = Join-Path $repositoryRoot 'tests\PiCompanion.ExplorerCommand.Smoke\PiCompanion.ExplorerCommand.Smoke.vcxproj'
$localDotnet = Join-Path $env:LOCALAPPDATA 'PiCompanionTools\dotnet\dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet).Source }

Push-Location $webSearchExtensionDirectory
try {
    & npm ci
    if ($LASTEXITCODE -ne 0) { throw "Web Search Extension npm ci failed with exit code $LASTEXITCODE." }

    & npm run build
    if ($LASTEXITCODE -ne 0) { throw "Web Search Extension build failed with exit code $LASTEXITCODE." }

    & npm test
    if ($LASTEXITCODE -ne 0) { throw "Web Search Extension tests failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}

Push-Location $chatDirectory
try {
    & npm ci
    if ($LASTEXITCODE -ne 0) { throw "npm ci failed with exit code $LASTEXITCODE." }

    & npm run build
    if ($LASTEXITCODE -ne 0) { throw "Agent Chat build failed with exit code $LASTEXITCODE." }

    & npm test
    if ($LASTEXITCODE -ne 0) { throw "Agent Chat tests failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}

& node --test (Join-Path $repositoryRoot 'tests\PiCompanion.Extension.Tests\pi-companion-extension.test.mjs')
if ($LASTEXITCODE -ne 0) { throw "Pi Companion Extension tests failed with exit code $LASTEXITCODE." }

& $dotnetCommand restore $solutionPath
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }

& $dotnetCommand build $solutionPath --configuration $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) {
    throw 'Visual Studio Build Tools with the x64 C++ workload is required to build Explorer integration.'
}

$visualStudioPath = & $vswhere `
    -latest `
    -products '*' `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath
if ([string]::IsNullOrWhiteSpace($visualStudioPath)) {
    throw 'The Visual Studio x64 C++ build tools are not installed.'
}

$nativeMsBuild = Join-Path $visualStudioPath 'MSBuild\Current\Bin\MSBuild.exe'
& $nativeMsBuild $explorerProject /m /p:Configuration=$Configuration /p:Platform=x64 /verbosity:minimal
if ($LASTEXITCODE -ne 0) { throw "Explorer command build failed with exit code $LASTEXITCODE." }

& $nativeMsBuild $explorerSmokeProject /m /p:Configuration=$Configuration /p:Platform=x64 /verbosity:minimal
if ($LASTEXITCODE -ne 0) { throw "Explorer command smoke-test build failed with exit code $LASTEXITCODE." }

$explorerCommandDll = Join-Path $repositoryRoot "src\PiCompanion.ExplorerCommand\bin\$Configuration\x64\PiCompanion.ExplorerCommand.dll"
$explorerCommandIcon = Join-Path $repositoryRoot "src\PiCompanion.ExplorerCommand\bin\$Configuration\x64\PiCompanion.ico"
$explorerSmokeExecutable = Join-Path $repositoryRoot "tests\PiCompanion.ExplorerCommand.Smoke\bin\$Configuration\x64\PiCompanion.ExplorerCommand.Smoke.exe"
& (Join-Path $PSScriptRoot 'new-pi-companion-icon.ps1') -Path $explorerCommandIcon
& $explorerSmokeExecutable $explorerCommandDll
if ($LASTEXITCODE -ne 0) { throw "Explorer command COM smoke test failed with exit code $LASTEXITCODE." }

& $dotnetCommand test $solutionPath --configuration $Configuration --no-build
if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE." }

Write-Host "Pi Companion $Configuration desktop, Explorer command, and tests completed successfully."
