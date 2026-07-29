[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Path
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (-not ('PiCompanionIconNativeMethods' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class PiCompanionIconNativeMethods
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr handle);
}
'@
}

$fullPath = [IO.Path]::GetFullPath($Path)
$directory = Split-Path -Parent $fullPath
if (![string]::IsNullOrWhiteSpace($directory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

$bitmap = [Drawing.Bitmap]::new(
    32,
    32,
    [Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
try {
    # Keep this visual aligned with PiCompanion.Desktop.Branding.PiAppIcon.
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.Clear([Drawing.Color]::Transparent)

    $background = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(23, 23, 23))
    $foreground = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(244, 244, 244))
    $font = [Drawing.Font]::new(
        'Georgia',
        22,
        [Drawing.FontStyle]::Bold,
        [Drawing.GraphicsUnit]::Pixel)
    $format = [Drawing.StringFormat]::new([Drawing.StringFormat]::GenericTypographic)
    try {
        $format.Alignment = [Drawing.StringAlignment]::Center
        $format.LineAlignment = [Drawing.StringAlignment]::Center
        $format.FormatFlags = [Drawing.StringFormatFlags]::NoWrap
        $graphics.FillEllipse($background, 0.5, 0.5, 31, 31)
        $graphics.DrawString(
            [string][char]0x03C0,
            $font,
            $foreground,
            [Drawing.RectangleF]::new(0, -1, 32, 34),
            $format)
    }
    finally {
        $format.Dispose()
        $font.Dispose()
        $foreground.Dispose()
        $background.Dispose()
    }

    $handle = $bitmap.GetHicon()
    try {
        $sourceIcon = [Drawing.Icon]::FromHandle($handle)
        $icon = [Drawing.Icon]$sourceIcon.Clone()
        $fileStream = [IO.File]::Create($fullPath)
        try {
            $icon.Save($fileStream)
        }
        finally {
            $fileStream.Dispose()
            $icon.Dispose()
            $sourceIcon.Dispose()
        }
    }
    finally {
        [PiCompanionIconNativeMethods]::DestroyIcon($handle) | Out-Null
    }
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}
