# Regenerates src/Roamwise/Assets/Roamwise.ico (multi-size, PNG-compressed entries) and the Linux app's PNG.
# Run from the repo root: powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot "..\src\Roamwise\Assets\Roamwise.ico"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
$sizes = 16, 20, 24, 32, 48, 64, 128, 256

function New-Frame([int]$n) {
    $bmp = New-Object System.Drawing.Bitmap $n, $n
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $s = $n / 64.0

    # Rounded tile in deep slate.
    $r = 14 * $s; $pad = 2 * $s; $w = $n - 2 * $pad
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($pad, $pad, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($pad + $w - 2 * $r, $pad, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($pad + $w - 2 * $r, $pad + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($pad, $pad + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x1F, 0x2A, 0x37))), $path)

    # Wi-Fi arcs.
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0xE9, 0xEB, 0xEE)), ([Math]::Max(1.5, 5 * $s))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $cx = 32 * $s; $cy = 47 * $s
    foreach ($rad in 12, 24) {
        $rr = $rad * $s
        $g.DrawArc($pen, $cx - $rr, $cy - $rr, 2 * $rr, 2 * $rr, 225, 90)
    }
    $d = 4.5 * $s
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xE9, 0xEB, 0xEE))), $cx - $d, $cy - $d, 2 * $d, 2 * $d)

    # The "best access point" marker.
    $m = 7 * $s
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x3F, 0xB6, 0x8B))), 46 * $s - $m, 17 * $s - $m, 2 * $m, 2 * $m)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$frames = foreach ($n in $sizes) { , (New-Frame $n) }

$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $n = $sizes[$i]; $len = $frames[$i].Length
    $bw.Write([byte]($(if ($n -ge 256) { 0 } else { $n })))
    $bw.Write([byte]($(if ($n -ge 256) { 0 } else { $n })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($f in $frames) { $bw.Write($f) }
$bw.Close()
Write-Host "Wrote $out"

# The Linux app and its AppImage use a 256 px PNG of the same artwork.
$png = Join-Path $PSScriptRoot "..\src\Roamwise.Linux\Assets\roamwise.png"
New-Item -ItemType Directory -Force (Split-Path $png) | Out-Null
[System.IO.File]::WriteAllBytes($png, $frames[$sizes.Count - 1])
Write-Host "Wrote $png"
