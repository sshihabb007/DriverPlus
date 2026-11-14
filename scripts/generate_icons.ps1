param(
    [string]$MasterLogoPath = "",
    [string]$PreviewOutDir = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$ProjectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$AssetsDir = Join-Path $ProjectRoot "assets"
if ([string]::IsNullOrWhiteSpace($MasterLogoPath)) {
    $MasterLogoPath = Join-Path $AssetsDir "driverplus.png"
}

if (-not (Test-Path -LiteralPath $MasterLogoPath)) {
    throw "Master logo not found at $MasterLogoPath"
}

function New-RoundedRectPath {
    param([float]$X, [float]$Y, [float]$W, [float]$H, [float]$Radius)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $Radius * 2.0
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc($X + $W - $d, $Y, $d, $d, 270, 90)
    $path.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90)
    $path.AddArc($X, $Y + $H - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Convert-BitmapToPngBytes {
    param([System.Drawing.Bitmap]$Bitmap)
    $ms = New-Object System.IO.MemoryStream
    try {
        $Bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,($ms.ToArray())
    }
    finally {
        $ms.Dispose()
    }
}

$masterBmp = [System.Drawing.Bitmap]::new($MasterLogoPath)
try {
    # Tight non-transparent crop of full master wordmark (14,24 .. 786,355)
    $fullCropRect = New-Object System.Drawing.Rectangle(14, 24, 773, 332)

    # Compact mark crops from master artwork for small taskbar/tray icons (16..32px):
    # 'D' with steering wheel & swoosh (x=14..194, y=92..254) + glowing '+' (x=654..786, y=102..236)
    $dRect = New-Object System.Drawing.Rectangle(14, 92, 180, 162)
    $plusRect = New-Object System.Drawing.Rectangle(654, 102, 133, 134)

    # 1. Generate UI-embedded crisp retina PNG (420px wide, transparent) -> assets/logo-ui.png
    $uiW = 420
    $uiH = [int][Math]::Round($uiW * ($fullCropRect.Height / [double]$fullCropRect.Width))
    $uiBmp = New-Object System.Drawing.Bitmap($uiW, $uiH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $gUi = [System.Drawing.Graphics]::FromImage($uiBmp)
        try {
            $gUi.Clear([System.Drawing.Color]::Transparent)
            $gUi.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $gUi.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $gUi.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $gUi.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $destRect = New-Object System.Drawing.Rectangle(0, 0, $uiW, $uiH)
            $gUi.DrawImage($masterBmp, $destRect, $fullCropRect, [System.Drawing.GraphicsUnit]::Pixel)
        }
        finally {
            $gUi.Dispose()
        }
        $uiOutPath = Join-Path $AssetsDir "logo-ui.png"
        [System.IO.File]::WriteAllBytes($uiOutPath, (Convert-BitmapToPngBytes $uiBmp))
    }
    finally {
        $uiBmp.Dispose()
    }

    # 2. Generate GitHub Light-Mode adaptive wordmark -> assets/driverplus-light.png
    # Maps neutral white/silver pixels to deep slate (#0F172A) while keeping electric-blue pixels untouched
    $lightBmp = New-Object System.Drawing.Bitmap($masterBmp.Width, $masterBmp.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        for ($y = 0; $y -lt $masterBmp.Height; $y++) {
            for ($x = 0; $x -lt $masterBmp.Width; $x++) {
                $c = $masterBmp.GetPixel($x, $y)
                if ($c.A -eq 0) {
                    $lightBmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(0, 0, 0, 0))
                    continue
                }
                $maxCh = [Math]::Max($c.R, [Math]::Max($c.G, $c.B))
                $minCh = [Math]::Min($c.R, [Math]::Min($c.G, $c.B))
                $sat = if ($maxCh -eq 0) { 0.0 } else { ($maxCh - $minCh) / [double]$maxCh }

                # Low saturation (white/light-gray text & road lines) -> transition smoothly to deep navy (#0F172A .. #334155)
                if ($sat -lt 0.22) {
                    $lum = (0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B) / 255.0
                    # Bright white (lum ~ 1.0) maps to crisp #0F172A (15,23,42); mid-gray road swoosh maps to #475569 (71,85,105)
                    $nr = [int][Math]::Round(71 - 56 * $lum)
                    $ng = [int][Math]::Round(85 - 62 * $lum)
                    $nb = [int][Math]::Round(105 - 63 * $lum)
                    $lightBmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($c.A, [Math]::Max(0,[Math]::Min(255,$nr)), [Math]::Max(0,[Math]::Min(255,$ng)), [Math]::Max(0,[Math]::Min(255,$nb))))
                }
                else {
                    # Saturated electric blue / cyan swooshes, steering wheel, and '+' stay vivid!
                    $lightBmp.SetPixel($x, $y, $c)
                }
            }
        }
        $lightOutPath = Join-Path $AssetsDir "driverplus-light.png"
        [System.IO.File]::WriteAllBytes($lightOutPath, (Convert-BitmapToPngBytes $lightBmp))
    }
    finally {
        $lightBmp.Dispose()
    }

    # 3. Generate multi-resolution Windows ICO (16, 24, 32, 48, 64, 128, 256) -> assets/driverplus.ico
    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    $icoEntries = @()

    foreach ($sz in $sizes) {
        # Render at 4x supersample (min 256px) then downsample cleanly for razor-sharp edges
        $super = [Math]::Max(256, $sz * 4)
        $canvas = New-Object System.Drawing.Bitmap($super, $super, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $g = [System.Drawing.Graphics]::FromImage($canvas)
            try {
                $g.Clear([System.Drawing.Color]::Transparent)
                $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

                $pad = $super * 0.04
                $box = $super - 2.0 * $pad
                $radius = $super * 0.22
                $bgPath = New-RoundedRectPath -X $pad -Y $pad -W $box -H $box -Radius $radius
                $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
                    (New-Object System.Drawing.PointF(0, 0)),
                    (New-Object System.Drawing.PointF($super, $super)),
                    [System.Drawing.Color]::FromArgb(255, 10, 17, 32),
                    [System.Drawing.Color]::FromArgb(255, 17, 30, 56)
                )
                $borderWidth = [Math]::Max(4.0, $super * 0.028)
                $borderPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(225, 14, 165, 233), $borderWidth)
                try {
                    $g.FillPath($bgBrush, $bgPath)
                    $g.DrawPath($borderPen, $bgPath)
                }
                finally {
                    $bgBrush.Dispose()
                    $borderPen.Dispose()
                    $bgPath.Dispose()
                }

                if ($sz -le 32) {
                    # Compact 'D+' emblem extracted directly from master artwork so 16..32px tray/taskbar icons are bold & legible
                    $dTargetH = [int][Math]::Round($super * 0.56)
                    $dTargetW = [int][Math]::Round($dTargetH * ($dRect.Width / [double]$dRect.Height))
                    $pTargetH = [int][Math]::Round($super * 0.40)
                    $pTargetW = [int][Math]::Round($pTargetH * ($plusRect.Width / [double]$plusRect.Height))
                    $gap = [int][Math]::Round($super * -0.02)
                    $totalW = $dTargetW + $gap + $pTargetW
                    $startX = [int][Math]::Round(($super - $totalW) / 2.0)
                    $dY = [int][Math]::Round(($super - $dTargetH) / 2.0) + [int][Math]::Round($super * 0.02)
                    $pY = [int][Math]::Round(($super - $pTargetH) / 2.0) - [int][Math]::Round($super * 0.02)

                    [int]$pX = $startX + $dTargetW + $gap
                    $g.DrawImage($masterBmp, [System.Drawing.Rectangle]::new($startX, $dY, $dTargetW, $dTargetH), $dRect, [System.Drawing.GraphicsUnit]::Pixel)
                    $g.DrawImage($masterBmp, [System.Drawing.Rectangle]::new($pX, $pY, $pTargetW, $pTargetH), $plusRect, [System.Drawing.GraphicsUnit]::Pixel)
                }
                else {
                    # Full official Driver+ horizontal logo with upper & lower blue highway arcs
                    $targetW = [int][Math]::Round($super * 0.86)
                    $targetH = [int][Math]::Round($targetW * ($fullCropRect.Height / [double]$fullCropRect.Width))
                    $dx = [int][Math]::Round(($super - $targetW) / 2.0)
                    $dy = [int][Math]::Round(($super - $targetH) / 2.0)
                    $g.DrawImage($masterBmp, [System.Drawing.Rectangle]::new($dx, $dy, $targetW, $targetH), $fullCropRect, [System.Drawing.GraphicsUnit]::Pixel)
                }
            }
            finally {
                $g.Dispose()
            }

            $finalBmp = New-Object System.Drawing.Bitmap($sz, $sz, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            try {
                $gf = [System.Drawing.Graphics]::FromImage($finalBmp)
                try {
                    $gf.Clear([System.Drawing.Color]::Transparent)
                    $gf.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                    $gf.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                    $gf.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                    $gf.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                    $gf.DrawImage($canvas, (New-Object System.Drawing.Rectangle(0, 0, $sz, $sz)))
                }
                finally {
                    $gf.Dispose()
                }
                $pngBytes = Convert-BitmapToPngBytes $finalBmp
                $icoEntries += ,@{ Size = $sz; Bytes = $pngBytes }

                if (-not [string]::IsNullOrWhiteSpace($PreviewOutDir)) {
                    New-Item -ItemType Directory -Force -Path $PreviewOutDir | Out-Null
                    [System.IO.File]::WriteAllBytes((Join-Path $PreviewOutDir "ico_$sz.png"), $pngBytes)
                }
            }
            finally {
                $finalBmp.Dispose()
            }
        }
        finally {
            $canvas.Dispose()
        }
    }

    # Assemble binary Windows ICO file
    $icoStream = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($icoStream)
    try {
        $bw.Write([uint16]0)
        $bw.Write([uint16]1)
        $bw.Write([uint16]$icoEntries.Count)

        $offset = 6 + ($icoEntries.Count * 16)
        foreach ($entry in $icoEntries) {
            $wByte = if ($entry.Size -ge 256) { [byte]0 } else { [byte]$entry.Size }
            $hByte = if ($entry.Size -ge 256) { [byte]0 } else { [byte]$entry.Size }
            $bw.Write($wByte)
            $bw.Write($hByte)
            $bw.Write([byte]0)
            $bw.Write([byte]0)
            $bw.Write([uint16]1)
            $bw.Write([uint16]32)
            $bw.Write([uint32]$entry.Bytes.Length)
            $bw.Write([uint32]$offset)
            $offset += $entry.Bytes.Length
        }
        foreach ($entry in $icoEntries) {
            [byte[]]$raw = $entry.Bytes
            $icoStream.Write($raw, 0, $raw.Length)
        }
        $bw.Flush()
        $icoPath = Join-Path $AssetsDir "driverplus.ico"
        [System.IO.File]::WriteAllBytes($icoPath, $icoStream.ToArray())
    }
    finally {
        $bw.Dispose()
        $icoStream.Dispose()
    }
}
finally {
    $masterBmp.Dispose()
}

Write-Host "Successfully generated driverplus.ico, logo-ui.png, and driverplus-light.png from master logo."
