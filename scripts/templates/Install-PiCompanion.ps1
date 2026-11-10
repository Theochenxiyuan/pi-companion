[CmdletBinding()]
param(
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'
$packageName = 'PiCompanion'
$scriptDir = $PSScriptRoot

$msixFiles = @(Get-ChildItem -LiteralPath $scriptDir -Filter 'PiCompanion-*-win-x64.msix')
if ($msixFiles.Count -eq 0) {
    throw '未在当前目录找到 Pi Companion 安装包（.msix）。'
}
if ($msixFiles.Count -gt 1) {
    throw "当前目录有多个安装包，请只保留要安装的那一个：$(($msixFiles | ForEach-Object Name) -join '、')"
}
$msixFile = $msixFiles[0]

function Get-MsixVersion([string]$Path) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $archive.GetEntry('AppxManifest.xml')
        if ($null -eq $entry) { throw '安装包缺少 AppxManifest.xml。' }
        $reader = New-Object IO.StreamReader($entry.Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        return [Version]$manifest.Package.Identity.Version
    }
    finally {
        $archive.Dispose()
    }
}

$cerFile = Get-ChildItem -LiteralPath $scriptDir -Filter 'PiCompanion-*-win-x64.cer' | Select-Object -First 1
if ($null -ne $cerFile) {
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cerFile.FullName)
    $trusted = Get-ChildItem -Path Cert:\LocalMachine\Root, Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $cert.Thumbprint } |
        Select-Object -First 1

    if ($null -eq $trusted) {
        Write-Host "正在请求管理员权限，以信任预览版签名证书（$($cert.Subject)）……" -ForegroundColor Cyan
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

# The development package has a different identity and would otherwise register a second app.
Get-AppxPackage -Name 'PiCompanion.Development' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "正在移除开发版 $($_.PackageFullName)……" -ForegroundColor Yellow
    Remove-AppxPackage -Package $_.PackageFullName
}

$newVersion = Get-MsixVersion $msixFile.FullName
$installed = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue | Select-Object -First 1
$addArguments = @{ Path = $msixFile.FullName; ForceApplicationShutdown = $true }
if ($null -ne $installed) {
    $installedVersion = [Version]$installed.Version
    if ($installedVersion -gt $newVersion) {
        throw "已安装更新的版本 $installedVersion，当前安装包是 $newVersion。如需降级，请先在系统设置中卸载 Pi Companion。"
    }
    if ($installedVersion -eq $newVersion) {
        $addArguments.ForceUpdateFromAnyVersion = $true
    }
    Write-Host "正在将 Pi Companion 从 $installedVersion 更新到 $newVersion……" -ForegroundColor Cyan
}
else {
    Write-Host "正在安装 Pi Companion $newVersion……" -ForegroundColor Cyan
}

# Update in place: removing the package first would delete any data Windows kept in its private folder.
Add-AppxPackage @addArguments

Write-Host 'Pi Companion 安装完成，可以从开始菜单或资源管理器右键菜单启动。' -ForegroundColor Green
if ($Launch) {
    $installed = Get-AppxPackage -Name $packageName | Select-Object -First 1
    if ($null -ne $installed) {
        Start-Process "shell:AppsFolder\$($installed.PackageFamilyName)!PiCompanion"
    }
}
