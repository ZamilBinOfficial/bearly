param([string]$Source)
# One-time setup: builds icons from the logo, validates the app, creates Desktop + Start Menu shortcuts.
$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$assets = Join-Path $Root 'assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null
Add-Type -AssemblyName System.Drawing

$srcPath = Join-Path $assets 'logo-source.jpg'
if ($Source) { Copy-Item -LiteralPath $Source -Destination $srcPath -Force }

$src = [System.Drawing.Image]::FromFile($srcPath)
$inset = [int]($src.Width * 0.018)
$crop = New-Object System.Drawing.Rectangle($inset, $inset, ($src.Width - 2 * $inset), ($src.Height - 2 * $inset))

function New-Rounded([int]$size) {
    $tmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $tg = [System.Drawing.Graphics]::FromImage($tmp)
    $tg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $tg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $tg.DrawImage($src, (New-Object System.Drawing.Rectangle(0, 0, $size, $size)), $crop, [System.Drawing.GraphicsUnit]::Pixel)
    $tg.Dispose()

    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    [single]$s = $size
    [single]$d = $s * 0.45
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc([single]0, [single]0, $d, $d, [single]180, [single]90)
    $path.AddArc([single]($s - $d), [single]0, $d, $d, [single]270, [single]90)
    $path.AddArc([single]($s - $d), [single]($s - $d), $d, $d, [single]0, [single]90)
    $path.AddArc([single]0, [single]($s - $d), $d, $d, [single]90, [single]90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.TextureBrush($tmp)
    $g.FillPath($brush, $path)
    $brush.Dispose(); $path.Dispose(); $g.Dispose(); $tmp.Dispose()
    return $bmp
}

# UI logo
$ui = New-Rounded 256
$ui.Save((Join-Path $assets 'logo-256.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$ui.Dispose()

# Multi-size PNG-compressed .ico
$sizes = @(256, 128, 64, 48, 32, 24, 16)
$blobs = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($sz in $sizes) {
    $b = New-Rounded $sz
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $blobs.Add($ms.ToArray())
    $ms.Dispose(); $b.Dispose()
}
$src.Dispose()

$icoPath = Join-Path $assets 'bearly.ico'
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $data = $blobs[$i]
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($data in $blobs) { $bw.Write($data, 0, $data.Length) }
$bw.Flush(); $bw.Dispose(); $fs.Dispose()
Write-Output "Icons built."

# Validate (compiles engine dll + parses UI without showing it)
$out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File (Join-Path $Root 'Bearly.ps1') -Validate 2>&1
Write-Output ($out | Out-String).Trim()

# Shortcuts
$ws = New-Object -ComObject WScript.Shell
$targets = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Bearly.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'Bearly.lnk')
)
foreach ($lnk in $targets) {
    $sc = $ws.CreateShortcut($lnk)
    $sc.TargetPath = "$env:WINDIR\System32\wscript.exe"
    $sc.Arguments = "`"$(Join-Path $Root 'Bearly.vbs')`""
    $sc.WorkingDirectory = $Root
    $sc.IconLocation = "$icoPath,0"
    $sc.Description = 'Bearly - Zero Mode and Deep Clean'
    $sc.Save()
}
Write-Output "Shortcuts created."
