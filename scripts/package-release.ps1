[CmdletBinding()]
param(
    [string]$Version = '0.5.0-preview',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Publisher = 'CN=Pi Companion',
    [string]$PfxPath,
    [string]$PfxPassword,
    [string]$CertificateThumbprint,
    [switch]$SkipSelfSignedCertificate,
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $repositoryRoot 'artifacts\release'
$cleanVersion = $Version.TrimStart('v', 'V')
$numericPart = ($cleanVersion -split '-')[0]
$versionSegments = @($numericPart -split '\.')
while ($versionSegments.Count -lt 4) {
    $versionSegments += '0'
}
$msixVersion = ($versionSegments[0..3] -join '.')

if (!$NoBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Pi Companion build failed with exit code $LASTEXITCODE." }
}

$windowsKitBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$sdkDirectories = @(Get-ChildItem -LiteralPath $windowsKitBin -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
    Sort-Object { [Version]$_.Name } -Descending)

$makeAppx = $sdkDirectories |
    ForEach-Object { Join-Path $_.FullName 'x64\makeappx.exe' } |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($makeAppx)) {
    throw 'Windows SDK makeappx.exe is required to package the release MSIX.'
}

$signTool = $sdkDirectories |
    ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($signTool)) {
    throw 'Windows SDK signtool.exe is required to sign the release package.'
}

$nodeCommand = Get-Command node -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $nodeCommand -or !(Test-Path -LiteralPath $nodeCommand.Source -PathType Leaf)) {
    throw 'Node.js executable is required to bundle the private Pi runtime.'
}

$npmCommand = Get-Command npm.cmd -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $npmCommand) {
    $npmCommand = Get-Command npm -ErrorAction SilentlyContinue | Select-Object -First 1
}
if ($null -eq $npmCommand) {
    throw 'npm is required to bundle the private Pi runtime.'
}

$webSearchPkgPath = Join-Path $repositoryRoot 'src\PiCompanion.WebSearchExtension\package.json'
$webSearchPkg = Get-Content -LiteralPath $webSearchPkgPath -Raw | ConvertFrom-Json
$piVersion = [string]$webSearchPkg.devDependencies.'@earendil-works/pi-coding-agent'
if ([string]::IsNullOrWhiteSpace($piVersion)) {
    throw "Unable to determine locked @earendil-works/pi-coding-agent version from $webSearchPkgPath."
}

$stagingRoot = Join-Path $artifactsRoot 'staging'
$publishDir = Join-Path $stagingRoot 'app'

if (Test-Path -LiteralPath $artifactsRoot) {
    & cmd.exe /c "rmdir /s /q `"$artifactsRoot`""
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

Write-Host "Publishing self-contained Pi Companion Desktop ($cleanVersion, MSIX $msixVersion)..." -ForegroundColor Cyan
& dotnet publish (Join-Path $repositoryRoot 'src\PiCompanion.Desktop\PiCompanion.Desktop.csproj') `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    "-p:PiCompanionFormalRelease=true" `
    "-p:Version=$cleanVersion" `
    "-p:AssemblyVersion=$msixVersion" `
    "-p:FileVersion=$msixVersion" `
    --output $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$devMarker = Join-Path $publishDir 'PiCompanion.Development'
if (Test-Path -LiteralPath $devMarker) {
    Remove-Item -LiteralPath $devMarker -Force
}

$nativeOutput = Join-Path $repositoryRoot "src\PiCompanion.ExplorerCommand\bin\$Configuration\x64"
Copy-Item -LiteralPath (Join-Path $nativeOutput 'PiCompanion.ExplorerCommand.dll') -Destination $publishDir -Force
Copy-Item -LiteralPath (Join-Path $nativeOutput 'PiCompanion.ico') -Destination $publishDir -Force

Write-Host "Bundling private Node.js and Pi Runtime $piVersion..." -ForegroundColor Cyan
$piRuntimeDir = Join-Path $publishDir 'PiRuntime'
New-Item -ItemType Directory -Path $piRuntimeDir -Force | Out-Null
Copy-Item -LiteralPath $nodeCommand.Source -Destination (Join-Path $piRuntimeDir 'node.exe') -Force

$piRuntimePkgJson = @{
    name = 'pi-companion-private-runtime'
    private = $true
    version = $cleanVersion
    dependencies = @{
        '@earendil-works/pi-coding-agent' = $piVersion
    }
} | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText(
    (Join-Path $piRuntimeDir 'package.json'),
    $piRuntimePkgJson,
    [Text.UTF8Encoding]::new($false))

Push-Location $piRuntimeDir
try {
    & $npmCommand.Source install --omit=dev --no-audit --no-fund --ignore-scripts
    if ($LASTEXITCODE -ne 0) {
        throw "Private Pi runtime npm install failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

$bundledCli = Join-Path $piRuntimeDir 'node_modules\@earendil-works\pi-coding-agent\dist\bundle\cli.js'
if (!(Test-Path -LiteralPath $bundledCli -PathType Leaf)) {
    throw "Bundled Pi runtime entry was not created at $bundledCli."
}

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

$certPath = Join-Path $artifactsRoot "PiCompanion-v$cleanVersion-win-x64.cer"
$signingThumbprint = $CertificateThumbprint

if ([string]::IsNullOrWhiteSpace($PfxPath) -and [string]::IsNullOrWhiteSpace($signingThumbprint) -and !$SkipSelfSignedCertificate) {
    Write-Host "Creating self-signed code signing certificate ($Publisher)..." -ForegroundColor Cyan
    $existingCert = Get-ChildItem -Path Cert:\CurrentUser\My |
        Where-Object {
            $_.Subject -eq $Publisher -and
            $_.NotAfter -gt (Get-Date).AddDays(30) -and
            $_.HasPrivateKey
        } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

    if ($null -eq $existingCert) {
        $existingCert = New-SelfSignedCertificate `
            -Type Custom `
            -Subject $Publisher `
            -KeyUsage DigitalSignature `
            -FriendlyName 'Pi Companion Preview Signing' `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') `
            -NotAfter (Get-Date).AddYears(3)
    }

    $signingThumbprint = $existingCert.Thumbprint
    Export-Certificate -Cert $existingCert -FilePath $certPath -Force | Out-Null
}
elseif (![string]::IsNullOrWhiteSpace($PfxPath)) {
    $resolvedPfx = [IO.Path]::GetFullPath($PfxPath)
    if (!(Test-Path -LiteralPath $resolvedPfx -PathType Leaf)) {
        throw "PFX file not found: $resolvedPfx"
    }
    $flags = [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable
    $x509 = [Security.Cryptography.X509Certificates.X509Certificate2]::new($resolvedPfx, $PfxPassword, $flags)
    $Publisher = $x509.Subject
    [IO.File]::WriteAllBytes($certPath, $x509.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    $x509.Dispose()
}

function Invoke-CodeSign {
    param([Parameter(Mandatory)] [string[]]$Files)

    if (![string]::IsNullOrWhiteSpace($PfxPath)) {
        $signArgs = @('sign', '/fd', 'SHA256', '/f', ([IO.Path]::GetFullPath($PfxPath)))
        if (![string]::IsNullOrWhiteSpace($PfxPassword)) {
            $signArgs += @('/p', $PfxPassword)
        }
        $signArgs += $Files
        & $signTool @signArgs | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE." }
    }
    elseif (![string]::IsNullOrWhiteSpace($signingThumbprint)) {
        $signArgs = @('sign', '/fd', 'SHA256', '/sha1', $signingThumbprint, '/s', 'My') + $Files
        & $signTool @signArgs | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE." }
    }
}

$binariesToSign = @(
    (Join-Path $publishDir 'PiCompanion.Desktop.exe'),
    (Join-Path $publishDir 'PiCompanion.Desktop.dll'),
    (Join-Path $publishDir 'PiCompanion.Application.dll'),
    (Join-Path $publishDir 'PiCompanion.Core.dll'),
    (Join-Path $publishDir 'PiCompanion.ExplorerCommand.dll')
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }

if ($binariesToSign.Count -gt 0 -and (![string]::IsNullOrWhiteSpace($PfxPath) -or ![string]::IsNullOrWhiteSpace($signingThumbprint))) {
    Write-Host "Signing application binaries..." -ForegroundColor Cyan
    Invoke-CodeSign -Files $binariesToSign
}

Write-Host "Building MSIX package..." -ForegroundColor Cyan
$assetDirectory = Join-Path $publishDir 'Assets'
New-Item -ItemType Directory -Path $assetDirectory -Force | Out-Null
New-PiCompanionLogo -Size 44 -Path (Join-Path $assetDirectory 'Square44x44Logo.png')
New-PiCompanionLogo -Size 150 -Path (Join-Path $assetDirectory 'Square150x150Logo.png')
New-PiCompanionLogo -Size 50 -Path (Join-Path $assetDirectory 'StoreLogo.png')

$manifestTemplate = [IO.File]::ReadAllText((Join-Path $repositoryRoot 'src\PiCompanion.Packaging\Package.Release.appxmanifest'))
$manifestContent = $manifestTemplate.Replace('__PUBLISHER__', $Publisher).Replace('__VERSION__', $msixVersion)
$apxManifestPath = Join-Path $publishDir 'AppxManifest.xml'
[IO.File]::WriteAllText(
    $apxManifestPath,
    $manifestContent,
    [Text.UTF8Encoding]::new($false))

$msixPath = Join-Path $artifactsRoot "PiCompanion-v$cleanVersion-win-x64.msix"
& $makeAppx pack /d $publishDir /p $msixPath /o | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "makeappx pack failed with exit code $LASTEXITCODE."
}

Remove-Item -LiteralPath $apxManifestPath -Force
Remove-Item -LiteralPath $assetDirectory -Recurse -Force

if (![string]::IsNullOrWhiteSpace($PfxPath) -or ![string]::IsNullOrWhiteSpace($signingThumbprint)) {
    Write-Host "Signing MSIX package..." -ForegroundColor Cyan
    Invoke-CodeSign -Files @($msixPath)
}

$installScriptPath = Join-Path $artifactsRoot 'Install-PiCompanion.ps1'
$installScriptContent = @'
[CmdletBinding()]
param(
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'
$scriptDir = $PSScriptRoot
$msixFile = Get-ChildItem -LiteralPath $scriptDir -Filter 'PiCompanion-*-win-x64.msix' | Select-Object -First 1
$cerFile = Get-ChildItem -LiteralPath $scriptDir -Filter 'PiCompanion-*-win-x64.cer' | Select-Object -First 1

if ($null -eq $msixFile) {
    throw "未在当前目录找到 PiCompanion MSIX 安装包。"
}

if ($null -ne $cerFile) {
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cerFile.FullName)
    $trusted = Get-ChildItem -Path Cert:\LocalMachine\Root, Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $cert.Thumbprint } |
        Select-Object -First 1

    if ($null -eq $trusted) {
        Write-Host "正在请求管理员权限以信任预览版签名证书 ($($cert.Subject))..." -ForegroundColor Cyan
        $importCmd = "Import-Certificate -FilePath '$($cerFile.FullName.Replace("'", "''"))' -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null"
        $proc = Start-Process -FilePath 'powershell.exe' -Verb RunAs -Wait -PassThru -ArgumentList @(
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-Command', $importCmd
        )
        if ($proc.ExitCode -ne 0) {
            throw "证书导入未完成（退出码 $($proc.ExitCode)）。"
        }
    }
}

foreach ($pkgName in @('PiCompanion.Development', 'PiCompanion')) {
    Get-AppxPackage -Name $pkgName -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "正在移除旧版本 $($_.PackageFullName)..." -ForegroundColor Yellow
        Remove-AppxPackage -Package $_.PackageFullName
    }
}

Write-Host "正在安装 $($msixFile.Name)..." -ForegroundColor Cyan
Add-AppxPackage -Path $msixFile.FullName

Write-Host "Pi Companion 安装完成！可从开始菜单或 Windows 11 资源管理器右键菜单启动。" -ForegroundColor Green
if ($Launch) {
    $installed = Get-AppxPackage -Name 'PiCompanion' | Select-Object -First 1
    if ($null -ne $installed) {
        Start-Process "shell:AppsFolder\$($installed.PackageFamilyName)!PiCompanion"
    }
}
'@
[IO.File]::WriteAllText($installScriptPath, $installScriptContent, [Text.UTF8Encoding]::new($true))

Write-Host "Building Portable ZIP archive..." -ForegroundColor Cyan
$portableZipPath = Join-Path $artifactsRoot "PiCompanion-v$cleanVersion-win-x64-portable.zip"
if (Test-Path -LiteralPath $portableZipPath) {
    Remove-Item -LiteralPath $portableZipPath -Force
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory(
    $publishDir,
    $portableZipPath,
    [IO.Compression.CompressionLevel]::Optimal,
    $false)

& cmd.exe /c "rmdir /s /q `"$stagingRoot`""

$outputs = @(Get-ChildItem -LiteralPath $artifactsRoot -File)
$checksumLines = foreach ($file in $outputs) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($file.Name)"
}
$checksumPath = Join-Path $artifactsRoot 'SHA256SUMS.txt'
[IO.File]::WriteAllLines($checksumPath, $checksumLines, [Text.UTF8Encoding]::new($false))

Write-Host "`nRelease artifacts created in $artifactsRoot :" -ForegroundColor Green
Get-ChildItem -LiteralPath $artifactsRoot -File | Select-Object Name, @{Name='SizeMB';Expression={[Math]::Round($_.Length / 1MB, 2)}} | Format-Table -AutoSize
