[CmdletBinding()]
param(
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$results = [System.Collections.Generic.List[object]]::new()

function Add-Check {
    param(
        [Parameter(Mandatory)] [string]$Id,
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [ValidateSet('pass', 'fail', 'warn')] [string]$Status,
        [Parameter(Mandatory)] [bool]$Required,
        [Parameter(Mandatory)] [string]$Detail,
        [string]$Remediation = ''
    )

    $results.Add([pscustomobject]@{
        id = $Id
        name = $Name
        status = $Status
        required = $Required
        detail = $Detail
        remediation = $Remediation
    })
}

function Get-PropertyValue {
    param(
        [AllowNull()] [object]$InputObject,
        [Parameter(Mandatory)] [string]$Name
    )

    if ($null -eq $InputObject) {
        return $null
    }

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Invoke-ExternalText {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [string[]]$Arguments = @()
    )

    try {
        $output = @(& $Path @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
        return [pscustomobject]@{
            succeeded = $exitCode -eq 0
            exitCode = $exitCode
            text = (($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine).Trim()
        }
    }
    catch {
        return [pscustomobject]@{
            succeeded = $false
            exitCode = -1
            text = $_.Exception.Message
        }
    }
}

function Parse-Version {
    param([AllowEmptyString()] [string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $normalized = (($Value.Trim() -split '\s+')[0]).TrimStart('v') -split '-', 2 | Select-Object -First 1
    $version = $null
    if ([Version]::TryParse([string]$normalized, [ref]$version)) {
        return $version
    }

    return $null
}

function Find-Command {
    param([Parameter(Mandatory)] [string]$Name)

    return Get-Command $Name -ErrorAction SilentlyContinue | Select-Object -First 1
}

function Find-WebView2Version {
    $registryRoots = @(
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients',
        'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients',
        'HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients'
    )
    $versions = [System.Collections.Generic.List[Version]]::new()

    foreach ($root in $registryRoots) {
        if (!(Test-Path -LiteralPath $root)) {
            continue
        }

        foreach ($key in @(Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue)) {
            $values = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction SilentlyContinue
            $name = [string](Get-PropertyValue $values 'name')
            $rawVersion = [string](Get-PropertyValue $values 'pv')
            if ($name -notlike '*WebView2*') {
                continue
            }

            $version = Parse-Version $rawVersion
            if ($null -ne $version) {
                $versions.Add($version)
            }
        }
    }

    return $versions | Sort-Object -Descending | Select-Object -First 1
}

$isWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
if (!$isWindows) {
    Add-Check 'windows' 'Windows 11' 'fail' $true '当前系统不是 Windows。' '请在 Windows 11 x64 机器上构建和运行。'
}
else {
    try {
        $windows = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
        $build = [int](Get-PropertyValue $windows 'CurrentBuildNumber')
        $displayVersion = [string](Get-PropertyValue $windows 'DisplayVersion')
        if ($build -ge 22000) {
            Add-Check 'windows' 'Windows 11' 'pass' $true "Windows $displayVersion (Build $build)"
        }
        else {
            Add-Check 'windows' 'Windows 11' 'fail' $true "Windows $displayVersion (Build $build)" '需要 Windows 11 Build 22000 或更高版本。'
        }
    }
    catch {
        Add-Check 'windows' 'Windows 11' 'fail' $true '无法读取 Windows 版本。' $_.Exception.Message
    }
}

if ([Environment]::Is64BitOperatingSystem) {
    Add-Check 'architecture' 'x64 系统' 'pass' $true '64-bit operating system'
}
else {
    Add-Check 'architecture' 'x64 系统' 'fail' $true '当前系统不是 64 位。' 'Pi Companion 目前仅支持 Windows 11 x64。'
}

$git = Find-Command 'git'
if ($null -eq $git) {
    Add-Check 'git' 'Git' 'fail' $true '未找到 git。' '运行 scripts/bootstrap.ps1 -InstallMissing，或安装 Git for Windows。'
}
else {
    $gitResult = Invoke-ExternalText $git.Source @('--version')
    if ($gitResult.succeeded) {
        Add-Check 'git' 'Git' 'pass' $true $gitResult.text
    }
    else {
        Add-Check 'git' 'Git' 'fail' $true 'git 无法正常运行。' $gitResult.text
    }
}

$node = Find-Command 'node'
if ($null -eq $node) {
    Add-Check 'node' 'Node.js 24' 'fail' $true '未找到 node。' '运行 scripts/bootstrap.ps1 -InstallMissing，或安装 Node.js 24 LTS。'
}
else {
    $nodeResult = Invoke-ExternalText $node.Source @('--version')
    $nodeVersion = Parse-Version $nodeResult.text
    if ($nodeResult.succeeded -and $null -ne $nodeVersion -and $nodeVersion.Major -eq 24) {
        Add-Check 'node' 'Node.js 24' 'pass' $true "v$nodeVersion"
    }
    else {
        Add-Check 'node' 'Node.js 24' 'fail' $true ($nodeResult.text | Out-String).Trim() '安装 Node.js 24 LTS；其他主版本尚未作为内测基线验证。'
    }
}

$npm = Find-Command 'npm'
if ($null -eq $npm) {
    Add-Check 'npm' 'npm 11' 'fail' $true '未找到 npm。' 'Node.js 24 LTS 会附带受支持的 npm。'
}
else {
    $npmResult = Invoke-ExternalText $npm.Source @('--version')
    $npmVersion = Parse-Version $npmResult.text
    if ($npmResult.succeeded -and $null -ne $npmVersion -and $npmVersion.Major -eq 11) {
        Add-Check 'npm' 'npm 11' 'pass' $true $npmVersion.ToString()
    }
    else {
        Add-Check 'npm' 'npm 11' 'fail' $true ($npmResult.text | Out-String).Trim() '安装 Node.js 24 LTS，或将 npm 更新到 11.x。'
    }
}

$globalJson = Get-Content -Raw -Encoding UTF8 (Join-Path $repositoryRoot 'global.json') | ConvertFrom-Json
$expectedDotnet = [Version]$globalJson.sdk.version
$localDotnet = if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    $null
}
else {
    Join-Path $env:LOCALAPPDATA 'PiCompanionTools\dotnet\dotnet.exe'
}
$dotnetPath = if ($null -ne $localDotnet -and (Test-Path -LiteralPath $localDotnet -PathType Leaf)) {
    $localDotnet
}
else {
    $dotnet = Find-Command 'dotnet'
    if ($null -eq $dotnet) { $null } else { $dotnet.Source }
}

if ($null -eq $dotnetPath) {
    Add-Check 'dotnet' ".NET SDK $expectedDotnet" 'fail' $true '未找到 dotnet。' '运行 scripts/bootstrap.ps1 -InstallMissing，或安装 .NET 10 SDK。'
}
else {
    $dotnetResult = Invoke-ExternalText $dotnetPath @('--version')
    $dotnetVersion = Parse-Version $dotnetResult.text
    $expectedFeatureBand = [Math]::Floor($expectedDotnet.Build / 100)
    $actualFeatureBand = if ($null -eq $dotnetVersion) { -1 } else { [Math]::Floor($dotnetVersion.Build / 100) }
    $compatible = $dotnetResult.succeeded -and
        $null -ne $dotnetVersion -and
        $dotnetVersion.Major -eq $expectedDotnet.Major -and
        $dotnetVersion.Minor -eq $expectedDotnet.Minor -and
        $actualFeatureBand -eq $expectedFeatureBand -and
        $dotnetVersion.Build -ge $expectedDotnet.Build
    if ($compatible) {
        Add-Check 'dotnet' ".NET SDK $expectedDotnet" 'pass' $true $dotnetVersion.ToString()
    }
    else {
        Add-Check 'dotnet' ".NET SDK $expectedDotnet" 'fail' $true ($dotnetResult.text | Out-String).Trim() "安装 global.json 要求的 .NET SDK $expectedDotnet（同一 feature band 的更新补丁也可用）。"
    }
}

$webSearchPackage = Get-Content -Raw -Encoding UTF8 (Join-Path $repositoryRoot 'src\PiCompanion.WebSearchExtension\package.json') | ConvertFrom-Json
$expectedPi = [string]$webSearchPackage.devDependencies.'@earendil-works/pi-coding-agent'
$pi = Find-Command 'pi'
if ($null -eq $pi) {
    Add-Check 'pi' "Pi Runtime $expectedPi" 'fail' $true '未找到 pi。' "运行 npm install --global @earendil-works/pi-coding-agent@$expectedPi。"
}
else {
    $piResult = Invoke-ExternalText $pi.Source @('--version')
    $piVersion = Parse-Version $piResult.text
    if ($piResult.succeeded -and $null -ne $piVersion -and $piVersion.ToString() -eq $expectedPi) {
        Add-Check 'pi' "Pi Runtime $expectedPi" 'pass' $true $piVersion.ToString()
    }
    else {
        Add-Check 'pi' "Pi Runtime $expectedPi" 'fail' $true ($piResult.text | Out-String).Trim() "运行 npm install --global @earendil-works/pi-coding-agent@$expectedPi。"
    }
}

$programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
$vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
$visualStudioPath = $null
if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
    $visualStudioPath = (& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null | Select-Object -First 1)
}

if ([string]::IsNullOrWhiteSpace([string]$visualStudioPath)) {
    Add-Check 'visual-studio' 'Visual Studio 2022 C++ Build Tools' 'fail' $true '未找到 x64 C++ 工具链。' '安装 Visual Studio 2022 Build Tools，并选择“使用 C++ 的桌面开发”工作负载及推荐组件。'
}
else {
    $msbuild = Join-Path ([string]$visualStudioPath) 'MSBuild\Current\Bin\MSBuild.exe'
    $vsVersion = (& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property catalog_productDisplayVersion 2>$null | Select-Object -First 1)
    if (Test-Path -LiteralPath $msbuild -PathType Leaf) {
        Add-Check 'visual-studio' 'Visual Studio 2022 C++ Build Tools' 'pass' $true "Build Tools $vsVersion"
    }
    else {
        Add-Check 'visual-studio' 'Visual Studio 2022 C++ Build Tools' 'fail' $true '已找到 Visual Studio，但缺少 MSBuild。' '通过 Visual Studio Installer 修复 Build Tools 安装。'
    }
}

$webView2Version = Find-WebView2Version
if ($null -eq $webView2Version) {
    Add-Check 'webview2' 'Microsoft Edge WebView2 Runtime' 'fail' $true '未找到 WebView2 Runtime。' '运行 scripts/bootstrap.ps1 -InstallMissing，或安装 Evergreen WebView2 Runtime。'
}
else {
    Add-Check 'webview2' 'Microsoft Edge WebView2 Runtime' 'pass' $true $webView2Version.ToString()
}

$winget = Find-Command 'winget'
if ($null -eq $winget) {
    Add-Check 'winget' 'WinGet' 'warn' $false '未找到 winget；自动补齐依赖不可用。' '从 Microsoft Store 安装或更新“应用安装程序”，也可以手动安装依赖。'
}
else {
    $wingetResult = Invoke-ExternalText $winget.Source @('--version')
    if ($wingetResult.succeeded) {
        Add-Check 'winget' 'WinGet' 'pass' $false $wingetResult.text
    }
    else {
        Add-Check 'winget' 'WinGet' 'warn' $false 'winget 无法正常运行。' $wingetResult.text
    }
}

$windowsKitBin = Join-Path $programFilesX86 'Windows Kits\10\bin'
$makeAppx = Get-ChildItem -LiteralPath $windowsKitBin -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
    Sort-Object { [Version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64\makeappx.exe' } |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace([string]$makeAppx)) {
    Add-Check 'makeappx' 'Windows SDK MakeAppx' 'warn' $false '未找到 makeappx.exe。' '仅安装 Explorer 右键菜单时需要；在 Visual Studio Installer 中添加推荐的 Windows SDK。'
}
else {
    Add-Check 'makeappx' 'Windows SDK MakeAppx' 'pass' $false 'Explorer 开发包验证工具可用。'
}

$developerPolicy = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -ErrorAction SilentlyContinue
$developerMode = (Get-PropertyValue $developerPolicy 'AllowDevelopmentWithoutDevLicense') -eq 1 -or
    (Get-PropertyValue $developerPolicy 'AllowAllTrustedApps') -eq 1
if ($developerMode) {
    Add-Check 'developer-mode' 'Windows 开发人员模式/旁加载' 'pass' $false 'Explorer 右键菜单开发包可以注册。'
}
else {
    Add-Check 'developer-mode' 'Windows 开发人员模式/旁加载' 'warn' $false '未启用。核心应用不受影响。' '仅安装未签名的 Explorer 右键菜单时需要在 Windows 设置中手动开启。'
}

$requiredFailures = @($results | Where-Object { $_.required -and $_.status -eq 'fail' }).Count
$warnings = @($results | Where-Object { $_.status -eq 'warn' }).Count
$summary = [pscustomobject]@{
    ready = $requiredFailures -eq 0
    requiredFailures = $requiredFailures
    warnings = $warnings
    expected = [pscustomobject]@{
        nodeMajor = 24
        npmMajor = 11
        dotnetSdk = $expectedDotnet.ToString()
        piRuntime = $expectedPi
    }
}

if ($Json) {
    [pscustomobject]@{
        checks = $results
        summary = $summary
    } | ConvertTo-Json -Depth 5
}
else {
    Write-Host 'Pi Companion source-beta environment check'
    Write-Host ''
    foreach ($check in $results) {
        $label = switch ($check.status) {
            'pass' { 'OK' }
            'fail' { 'FAIL' }
            default { 'WARN' }
        }
        $color = switch ($check.status) {
            'pass' { 'Green' }
            'fail' { 'Red' }
            default { 'Yellow' }
        }
        Write-Host ("[{0}] {1}: {2}" -f $label, $check.name, $check.detail) -ForegroundColor $color
        if (![string]::IsNullOrWhiteSpace($check.remediation) -and $check.status -ne 'pass') {
            Write-Host ("       -> {0}" -f $check.remediation)
        }
    }

    Write-Host ''
    if ($summary.ready) {
        Write-Host "Environment ready. Optional warnings: $warnings." -ForegroundColor Green
    }
    else {
        Write-Host "Environment not ready. Required failures: $requiredFailures; optional warnings: $warnings." -ForegroundColor Red
    }
}

if ($requiredFailures -gt 0) {
    exit 1
}

exit 0
