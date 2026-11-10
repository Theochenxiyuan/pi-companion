[CmdletBinding()]
param()

# Windows PowerShell 5.1 decodes BOM-less scripts with the system code page,
# which corrupts non-ASCII text and can break string literals.
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$excluded = '\\(node_modules|artifacts|bin|obj|\.dotnet-cli)\\'

$failures = @(Get-ChildItem -LiteralPath $repositoryRoot -Recurse -File -Filter '*.ps1' |
    Where-Object { $_.FullName -notmatch $excluded } |
    Where-Object {
        $bytes = [IO.File]::ReadAllBytes($_.FullName)
        $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
        -not $hasBom -and @($bytes | Where-Object { $_ -gt 0x7F }).Count -gt 0
    })

if ($failures.Count -gt 0) {
    $paths = $failures | ForEach-Object { $_.FullName.Substring($repositoryRoot.Length + 1) }
    throw "These PowerShell scripts contain non-ASCII text but are not saved as UTF-8 with BOM:`n$($paths -join "`n")"
}

Write-Host 'PowerShell script encoding check passed.' -ForegroundColor Green
