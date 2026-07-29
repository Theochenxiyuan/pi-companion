[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'
$packages = Get-AppxPackage |
    Where-Object { $_.Name -eq 'PiCompanion.Development' }
foreach ($package in $packages) {
    if ($PSCmdlet.ShouldProcess($package.PackageFullName, 'Remove Pi Companion development package')) {
        Remove-AppxPackage -Package $package.PackageFullName
    }
}

Write-Host 'Pi Companion Explorer integration removed for the current user.'
Write-Host 'Restart File Explorer or sign out and back in to unload the command.'
