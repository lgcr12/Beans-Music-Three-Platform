param(
    [string]$Source = "$PSScriptRoot\..\Assets\Player\Backgrounds\lyrics-hero-v2.png",
    [string]$Person = "$PSScriptRoot\..\Assets\Player\Overlays\lyrics-hero-v2-person.png",
    [string]$Background = "$PSScriptRoot\..\Assets\Player\Backgrounds\lyrics-hero-v2-depth.png"
)

Add-Type -AssemblyName System.Drawing
$sourceBitmap = [System.Drawing.Bitmap]::new($Source)
$width = $sourceBitmap.Width
$height = $sourceBitmap.Height
$personBitmap = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$cleanBitmap = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

# The mask follows the existing singer, microphone and coat silhouette. A 12px feather
# keeps the extracted edge free of a bright halo when it moves over the repaired layer.
$points = [System.Drawing.Point[]]@(
    [System.Drawing.Point]::new(0,220), [System.Drawing.Point]::new(34,177), [System.Drawing.Point]::new(95,125),
    [System.Drawing.Point]::new(170,92), [System.Drawing.Point]::new(278,86), [System.Drawing.Point]::new(373,122),
    [System.Drawing.Point]::new(440,181), [System.Drawing.Point]::new(480,263), [System.Drawing.Point]::new(510,330),
    [System.Drawing.Point]::new(586,365), [System.Drawing.Point]::new(616,430), [System.Drawing.Point]::new(631,510),
    [System.Drawing.Point]::new(650,620), [System.Drawing.Point]::new(696,720), [System.Drawing.Point]::new(674,870),
    [System.Drawing.Point]::new(622,1024), [System.Drawing.Point]::new(0,1024)
)
$mask = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$maskGraphics = [System.Drawing.Graphics]::FromImage($mask)
$maskGraphics.Clear([System.Drawing.Color]::Transparent)
$path = [System.Drawing.Drawing2D.GraphicsPath]::new()
$path.AddPolygon($points)
$maskGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$maskGraphics.FillPath([System.Drawing.Brushes]::White, $path)
$maskGraphics.Dispose()

# Draw the original into the person layer and apply the feathered alpha mask.
$personGraphics = [System.Drawing.Graphics]::FromImage($personBitmap)
$personGraphics.DrawImageUnscaled($sourceBitmap, 0, 0)
$personGraphics.Dispose()
$maskData = $mask.LockBits([System.Drawing.Rectangle]::new(0,0,$width,$height), [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$personData = $personBitmap.LockBits([System.Drawing.Rectangle]::new(0,0,$width,$height), [System.Drawing.Imaging.ImageLockMode]::ReadWrite, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$maskStride = $maskData.Stride; $personStride = $personData.Stride
for ($y=0; $y -lt $height; $y++) {
    for ($x=0; $x -lt $width; $x++) {
        $maskAlpha = [System.Runtime.InteropServices.Marshal]::ReadByte($maskData.Scan0, $y*$maskStride + $x*4 + 3)
        if ($maskAlpha -lt 255) {
            [System.Runtime.InteropServices.Marshal]::WriteByte($personData.Scan0, $y*$personStride + $x*4 + 3, $maskAlpha)
        }
    }
}
$mask.UnlockBits($maskData); $personBitmap.UnlockBits($personData)

# Make a quiet repaired background by cloning the unobstructed lake/sky area into the
# masked silhouette. The player overlay darkens this area, so the transition remains soft.
$cleanGraphics = [System.Drawing.Graphics]::FromImage($cleanBitmap)
$cleanGraphics.DrawImageUnscaled($sourceBitmap, 0, 0)
$cleanGraphics.SetClip($path)
$cleanGraphics.DrawImage($sourceBitmap, [System.Drawing.Rectangle]::new(0,0,$width,$height), [System.Drawing.Rectangle]::new(620,0,[Math]::Min($width-620,$width),$height), [System.Drawing.GraphicsUnit]::Pixel)
$cleanGraphics.ResetClip(); $cleanGraphics.Dispose()

$directory = Split-Path -Parent $Person; New-Item -ItemType Directory -Force -Path $directory | Out-Null
$directory = Split-Path -Parent $Background; New-Item -ItemType Directory -Force -Path $directory | Out-Null
$personBitmap.Save($Person, [System.Drawing.Imaging.ImageFormat]::Png)
$cleanBitmap.Save($Background, [System.Drawing.Imaging.ImageFormat]::Png)
$path.Dispose(); $mask.Dispose(); $sourceBitmap.Dispose(); $personBitmap.Dispose(); $cleanBitmap.Dispose()
Write-Output "Created $Person and $Background"
