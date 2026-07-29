[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$stagingDirectory = Join-Path $artifactsRoot "explorer-integration\$Configuration"
$desktopOutput = Join-Path $repositoryRoot "src\PiCompanion.Desktop\bin\$Configuration\net10.0-windows10.0.22000.0"
$nativeOutput = Join-Path $repositoryRoot "src\PiCompanion.ExplorerCommand\bin\$Configuration\x64"
$manifestSource = Join-Path $repositoryRoot 'src\PiCompanion.Packaging\Package.appxmanifest'

if (!$NoBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Pi Companion build failed with exit code $LASTEXITCODE." }
}

$requiredFiles = @(
    (Join-Path $desktopOutput 'PiCompanion.Desktop.exe'),
    (Join-Path $desktopOutput 'PiExtension\pi-companion.mjs'),
    (Join-Path $desktopOutput 'PiExtension\pi-web-search.mjs'),
    (Join-Path $desktopOutput 'PiExtension\pi-web-search.mjs.LEGAL.txt'),
    (Join-Path $desktopOutput 'THIRD-PARTY-NOTICES.md'),
    (Join-Path $nativeOutput 'PiCompanion.ExplorerCommand.dll'),
    (Join-Path $nativeOutput 'PiCompanion.ico'),
    $manifestSource
)
foreach ($requiredFile in $requiredFiles) {
    if (!(Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required Explorer integration artifact is missing: $requiredFile"
    }
}

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
if (Test-Path -LiteralPath $stagingDirectory) {
    $resolvedArtifactsRoot = [IO.Path]::GetFullPath($artifactsRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $resolvedStagingDirectory = [IO.Path]::GetFullPath($stagingDirectory)
    if (!$resolvedStagingDirectory.StartsWith(
        "$resolvedArtifactsRoot$([IO.Path]::DirectorySeparatorChar)",
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to replace staging directory outside the repository artifacts root: $resolvedStagingDirectory"
    }

    Remove-Item -LiteralPath $resolvedStagingDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $desktopOutput '*') -Destination $stagingDirectory -Recurse -Force
Copy-Item -LiteralPath (Join-Path $nativeOutput 'PiCompanion.ExplorerCommand.dll') -Destination $stagingDirectory -Force
Copy-Item -LiteralPath (Join-Path $nativeOutput 'PiCompanion.ico') -Destination $stagingDirectory -Force
Copy-Item -LiteralPath $manifestSource -Destination (Join-Path $stagingDirectory 'AppxManifest.xml') -Force

$assetDirectory = Join-Path $stagingDirectory 'Assets'
New-Item -ItemType Directory -Path $assetDirectory -Force | Out-Null
Add-Type -AssemblyName System.Drawing

function New-PiCompanionLogo {
    param(
        [Parameter(Mandatory)] [int]$Size,
        [Parameter(Mandatory)] [string]$Path
    )

    $bitmap = [Drawing.Bitmap]::new($Size, $Size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([Drawing.Color]::FromArgb(16, 16, 16))
        $fontSize = [Math]::Max(12, [Math]::Round($Size * 0.56))
        $font = [Drawing.Font]::new('Georgia', $fontSize, [Drawing.FontStyle]::Bold, [Drawing.GraphicsUnit]::Pixel)
        $brush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(237, 237, 237))
        try {
            $format = [Drawing.StringFormat]::new()
            $format.Alignment = [Drawing.StringAlignment]::Center
            $format.LineAlignment = [Drawing.StringAlignment]::Center
            $graphics.DrawString([char]0x03C0, $font, $brush, [Drawing.RectangleF]::new(0, 0, $Size, $Size), $format)
            $format.Dispose()
        }
        finally {
            $brush.Dispose()
            $font.Dispose()
        }

        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

New-PiCompanionLogo -Size 44 -Path (Join-Path $assetDirectory 'Square44x44Logo.png')
New-PiCompanionLogo -Size 150 -Path (Join-Path $assetDirectory 'Square150x150Logo.png')
New-PiCompanionLogo -Size 50 -Path (Join-Path $assetDirectory 'StoreLogo.png')

$windowsKitBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$makeAppx = Get-ChildItem -LiteralPath $windowsKitBin -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
    Sort-Object { [Version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64\makeappx.exe' } |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($makeAppx)) {
    throw 'Windows SDK makeappx.exe is required to validate the Explorer integration manifest.'
}

$packagePath = Join-Path $artifactsRoot 'PiCompanion.Development.msix'
& $makeAppx pack /d $stagingDirectory /p $packagePath /o | Out-Host
if ($LASTEXITCODE -ne 0) { throw "makeappx validation failed with exit code $LASTEXITCODE." }

Write-Output $stagingDirectory
