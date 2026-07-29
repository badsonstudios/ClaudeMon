<#
.SYNOPSIS
    One-off generator for the ClaudeMon application icon (issue #108).

.DESCRIPTION
    Renders the app mark at five sizes and writes a multi-resolution .ico to
    src/ClaudeMon/Resources/ClaudeMon.ico.

    THIS SCRIPT IS NOT PART OF THE BUILD. It is not referenced by any .csproj, by
    ClaudeMon.slnx, or by CI. The committed .ico is the source of truth that ships;
    this script exists so the artwork can be regenerated or tweaked deliberately.
    Regeneration produces byte-different output, so only run it when the design
    actually changes.

    PowerShell rather than bash (which CLAUDE.md prefers) because this is GDI+
    rendering — the "Windows-specific, bash genuinely can't do it" exception.
    Windows PowerShell 5.1 has System.Drawing in-box, so there are no dependencies.

    Why the container is written by hand: System.Drawing can save a single-image
    .ico only. A multi-resolution icon needs the ICONDIR / ICONDIRENTRY structure
    assembled manually.

.NOTES
    The mark: a rounded "clay" tile with a white "C", keeping the tray icon's
    tile-plus-white-glyph silhouette. The clay (#D97757) is deliberately OUTSIDE the
    four status colours used by IconRenderer (green/yellow/orange/red) — those each
    signal a usage level, and a static app icon painted in one of them would
    permanently mis-signal a state.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\icon\generate-claudemon-icon.ps1
#>
[CmdletBinding()]
param(
    # Defaults to <repo>/src/ClaudeMon/Resources/ClaudeMon.ico
    [string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

if (-not $OutputPath) {
    $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $OutputPath = Join-Path $repoRoot 'src\ClaudeMon\Resources\ClaudeMon.ico'
}

# --- The mark -------------------------------------------------------------------

# Claude clay. Mid-tone on purpose: it holds contrast against both a light (#F3F3F3)
# and a dark (#202020) taskbar.
$TileColor = [System.Drawing.Color]::FromArgb(0xFF, 0xD9, 0x77, 0x57)
$GlyphColor = [System.Drawing.Color]::White
# 1px inner stroke so the tile separates from a similarly-toned light background.
$EdgeColor = [System.Drawing.Color]::FromArgb(46, 0, 0, 0)   # ~18% black

# size, margin, corner radius, draw the accent strip?
# Margin is 0 at 16/24 so the mark stays full-bleed exactly like the tray icon; the
# larger sizes get breathing room so they don't look crowded in Alt-Tab.
# The accent strip is omitted below 32 where a 1-2px strip smears into mud.
$Specs = @(
    [pscustomobject]@{ Size = 16;  Margin = 0;  Radius = 2;  Accent = $false }
    [pscustomobject]@{ Size = 24;  Margin = 0;  Radius = 3;  Accent = $false }
    [pscustomobject]@{ Size = 32;  Margin = 1;  Radius = 4;  Accent = $true  }
    [pscustomobject]@{ Size = 48;  Margin = 2;  Radius = 6;  Accent = $true  }
    [pscustomobject]@{ Size = 256; Margin = 10; Radius = 32; Accent = $true  }
)

function New-RoundedRectPath {
    param([float] $X, [float] $Y, [float] $W, [float] $H, [float] $R)

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    # Degenerate radius: a plain rectangle (AddArc with d=0 throws).
    if ($R -le 0) {
        $path.AddRectangle((New-Object System.Drawing.RectangleF $X, $Y, $W, $H))
        return $path
    }
    $d = $R * 2
    $path.AddArc($X, $Y, $d, $d, 180, 90)                       # top-left
    $path.AddArc(($X + $W - $d), $Y, $d, $d, 270, 90)           # top-right
    $path.AddArc(($X + $W - $d), ($Y + $H - $d), $d, $d, 0, 90) # bottom-right
    $path.AddArc($X, ($Y + $H - $d), $d, $d, 90, 90)            # bottom-left
    $path.CloseFigure()
    return $path
}

function Get-GlyphFamily {
    # Segoe UI Semibold is a distinct family on Windows 10/11. Fall back to bold
    # Segoe UI on the (unlikely) machine that lacks it.
    foreach ($name in @('Segoe UI Semibold', 'Segoe UI')) {
        try { return (New-Object System.Drawing.FontFamily $name) } catch { }
    }
    return [System.Drawing.FontFamily]::GenericSansSerif
}

function New-TileBitmap {
    param([int] $Size, [int] $Margin, [float] $Radius, [bool] $Accent)

    $bmp = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        # AntiAliasGridFit, never ClearType: subpixel fringes are wrong on a bitmap
        # with a transparent margin (IconRenderer reasons the same way).
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $g.Clear([System.Drawing.Color]::Transparent)

        $tile = $Size - (2 * $Margin)

        # --- tile ---
        $tilePath = New-RoundedRectPath -X $Margin -Y $Margin -W $tile -H $tile -R $Radius
        try {
            $brush = New-Object System.Drawing.SolidBrush $TileColor
            try { $g.FillPath($brush, $tilePath) } finally { $brush.Dispose() }

            $pen = New-Object System.Drawing.Pen $EdgeColor, 1
            try {
                # Inset by half a pixel so the 1px stroke lands inside the fill.
                $inner = New-RoundedRectPath -X ($Margin + 0.5) -Y ($Margin + 0.5) `
                    -W ($tile - 1) -H ($tile - 1) -R ([Math]::Max(0, $Radius - 0.5))
                try { $g.DrawPath($pen, $inner) } finally { $inner.Dispose() }
            } finally { $pen.Dispose() }
        } finally { $tilePath.Dispose() }

        # --- accent geometry ---
        # Worked out before the glyph so the glyph can be centred in the space that is
        # actually left over. Sharing the full tile between both made the "C" sit on
        # top of the strip at 32 and 48.
        $stripH = 0; $stripSegW = 0; $stripGap = 0; $stripX = 0; $stripY = 0
        if ($Accent) {
            $stripH = [Math]::Max(1, [int][Math]::Round($Size * 0.05))
            $stripGap = [Math]::Max(1, [int][Math]::Round($Size * 0.022))
            $totalW = [int][Math]::Round($tile * 0.46)
            $stripSegW = [int][Math]::Floor(($totalW - (2 * $stripGap)) / 3)
            if ($stripSegW -lt 1) {
                $stripH = 0   # no room; fall back to a plain tile
            } else {
                $stripX = ($Size - (($stripSegW * 3) + ($stripGap * 2))) / 2
                $stripY = $Size - $Margin - $stripH - [Math]::Max(1, [int][Math]::Round($Size * 0.09))
            }
        }
        # Vertical space the glyph may use: the tile, less the strip and its breathing room.
        $glyphBoxH = if ($stripH -gt 0) { $stripY - $Margin } else { $tile }

        # --- glyph ---
        # Built at a nominal em size then scaled by its own bounding box. Centring on
        # MeasureString's line box instead would include leading and sit a single
        # letterform visibly low — very obvious at 16px.
        $family = Get-GlyphFamily
        try {
            $glyph = New-Object System.Drawing.Drawing2D.GraphicsPath
            try {
                $style = if ($family.IsStyleAvailable([System.Drawing.FontStyle]::Regular)) {
                    [System.Drawing.FontStyle]::Regular
                } else {
                    [System.Drawing.FontStyle]::Bold
                }
                $glyph.AddString('C', $family, [int] $style, 100,
                    (New-Object System.Drawing.PointF 0, 0),
                    [System.Drawing.StringFormat]::GenericTypographic)

                $b = $glyph.GetBounds()
                if ($b.Height -gt 0 -and $b.Width -gt 0) {
                    # Cap height as a fraction of the space the glyph actually owns.
                    $target = $glyphBoxH * 0.62
                    $scale = $target / $b.Height
                    $m = New-Object System.Drawing.Drawing2D.Matrix
                    try {
                        # Order matters: translate to origin, scale, then centre.
                        $m.Scale($scale, $scale)
                        $m.Translate(-$b.X, -$b.Y)
                        $glyph.Transform($m)
                    } finally { $m.Dispose() }

                    $b2 = $glyph.GetBounds()
                    $m2 = New-Object System.Drawing.Drawing2D.Matrix
                    try {
                        # Horizontally centred on the tile; vertically centred in the
                        # glyph box (which stops above the accent strip when there is one).
                        $m2.Translate(
                            (($Size - $b2.Width) / 2) - $b2.X,
                            ($Margin + (($glyphBoxH - $b2.Height) / 2)) - $b2.Y)
                        $glyph.Transform($m2)
                    } finally { $m2.Dispose() }

                    $gb = New-Object System.Drawing.SolidBrush $GlyphColor
                    try { $g.FillPath($gb, $glyph) } finally { $gb.Dispose() }
                }
            } finally { $glyph.Dispose() }
        } finally { $family.Dispose() }

        # --- accent strip (>=32 only): a static echo of the bar-style taskbar readout ---
        if ($stripH -gt 0) {
            for ($i = 0; $i -lt 3; $i++) {
                # Two of three filled: "roughly two-thirds".
                $alpha = if ($i -lt 2) { 150 } else { 60 }
                $c = [System.Drawing.Color]::FromArgb($alpha, 255, 255, 255)
                $sb = New-Object System.Drawing.SolidBrush $c
                try {
                    $x = $stripX + ($i * ($stripSegW + $stripGap))
                    $g.FillRectangle($sb, [float]$x, [float]$stripY, [float]$stripSegW, [float]$stripH)
                } finally { $sb.Dispose() }
            }
        }
    } finally {
        $g.Dispose()
    }
    return $bmp
}

# --- ICO container --------------------------------------------------------------

function Get-BmpPayload {
    <#
      A BITMAPINFOHEADER DIB, as embedded in an .ico:
        - biHeight is DOUBLE the real height (XOR bitmap + AND mask are stacked).
          Getting this wrong is the single most common hand-rolled-ICO bug.
        - The XOR bitmap is bottom-up BGRA.
        - The AND mask is 1bpp, each row padded to 4 bytes, all zeros ("show the XOR
          pixel"); alpha does the real work, but the mask must be present and
          correctly sized or legacy paths render garbage.
    #>
    param([System.Drawing.Bitmap] $Bitmap)

    $s = $Bitmap.Width
    $flipped = $Bitmap.Clone()
    try {
        $flipped.RotateFlip([System.Drawing.RotateFlipType]::RotateNoneFlipY)
        $rect = New-Object System.Drawing.Rectangle 0, 0, $s, $s
        $data = $flipped.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            # 32bpp rows are inherently 4-byte aligned, so stride == s*4.
            $xor = New-Object byte[] ($data.Stride * $s)
            [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $xor, 0, $xor.Length)
        } finally {
            $flipped.UnlockBits($data)
        }
    } finally {
        $flipped.Dispose()
    }

    $maskRow = [int][Math]::Floor((($s + 31) / 32)) * 4
    $mask = New-Object byte[] ($maskRow * $s)   # all zero

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    try {
        $bw.Write([uint32] 40)        # biSize
        $bw.Write([int32] $s)         # biWidth
        $bw.Write([int32] ($s * 2))   # biHeight -- XOR + AND stacked
        $bw.Write([uint16] 1)         # biPlanes
        $bw.Write([uint16] 32)        # biBitCount
        $bw.Write([uint32] 0)         # biCompression = BI_RGB
        $bw.Write([uint32] 0)         # biSizeImage
        $bw.Write([int32] 0)          # biXPelsPerMeter
        $bw.Write([int32] 0)          # biYPelsPerMeter
        $bw.Write([uint32] 0)         # biClrUsed
        $bw.Write([uint32] 0)         # biClrImportant
        $bw.Write($xor)
        $bw.Write($mask)
        $bw.Flush()
        return $ms.ToArray()
    } finally {
        $bw.Dispose(); $ms.Dispose()
    }
}

function Get-PngPayload {
    param([System.Drawing.Bitmap] $Bitmap)
    $ms = New-Object System.IO.MemoryStream
    try {
        $Bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        return $ms.ToArray()
    } finally { $ms.Dispose() }
}

Write-Host "Rendering $($Specs.Count) sizes..."

$payloads = @()
foreach ($spec in $Specs) {
    $bmp = New-TileBitmap -Size $spec.Size -Margin $spec.Margin -Radius $spec.Radius -Accent $spec.Accent
    try {
        # Only the 256 entry is PNG-compressed; the small sizes stay BMP for maximum
        # compatibility with older icon consumers.
        $bytes = if ($spec.Size -ge 256) { Get-PngPayload $bmp } else { Get-BmpPayload $bmp }
        $payloads += [pscustomobject]@{ Size = $spec.Size; Bytes = $bytes }
        Write-Host ("  {0,3}px -> {1,7:N0} bytes ({2})" -f $spec.Size, $bytes.Length,
            $(if ($spec.Size -ge 256) { 'PNG' } else { 'BMP32' }))
    } finally {
        $bmp.Dispose()
    }
}

$dir = Split-Path -Parent $OutputPath
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

$fs = New-Object System.IO.FileStream $OutputPath, ([System.IO.FileMode]::Create), ([System.IO.FileAccess]::Write)
$w = New-Object System.IO.BinaryWriter $fs
try {
    $w.Write([uint16] 0)                    # reserved
    $w.Write([uint16] 1)                    # type = icon
    $w.Write([uint16] $payloads.Count)      # count

    $offset = 6 + (16 * $payloads.Count)
    foreach ($p in $payloads) {
        # 256 is encoded as 0 in the single width/height bytes.
        $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
        $w.Write([byte] $dim)
        $w.Write([byte] $dim)
        $w.Write([byte] 0)                  # colorCount (0 = truecolour)
        $w.Write([byte] 0)                  # reserved
        # planes/bitCount are set on every entry, including the PNG one -- that is
        # what real authoring tools emit and what Roslyn and the shell read back.
        $w.Write([uint16] 1)
        $w.Write([uint16] 32)
        $w.Write([uint32] $p.Bytes.Length)
        $w.Write([uint32] $offset)
        $offset += $p.Bytes.Length
    }
    # Explicit cast + (offset, count) overload: a byte[] reached through a
    # pscustomobject property arrives PSObject-wrapped, and PowerShell then binds
    # Write($bytes) to a scalar overload and emits a single byte per payload.
    foreach ($p in $payloads) {
        $raw = [byte[]] $p.Bytes
        $w.Write($raw, 0, $raw.Length)
    }
    $w.Flush()
} finally {
    $w.Dispose(); $fs.Dispose()
}

Write-Host "Wrote $OutputPath ($((Get-Item $OutputPath).Length) bytes)"

# --- Sanity checks (read-only) --------------------------------------------------

$icon = New-Object System.Drawing.Icon $OutputPath
try { Write-Host "  loads OK; default size $($icon.Width)x$($icon.Height)" } finally { $icon.Dispose() }

# The BMP entries are selectable through System.Drawing. The 256 entry deliberately
# is NOT checked this way: System.Drawing.Icon's selector ignores PNG-compressed
# entries and falls back to the largest BMP, which is a limitation of that class,
# not of the container (the Windows shell has read PNG-in-ICO since Vista). It is
# validated structurally below instead.
foreach ($size in @(16, 24, 32, 48)) {
    $i = New-Object System.Drawing.Icon $OutputPath, $size, $size
    try {
        if ($i.Width -ne $size) { throw "size $size resolved to $($i.Width)" }
        Write-Host "  ${size}px entry present"
    } finally { $i.Dispose() }
}

$bytes = [System.IO.File]::ReadAllBytes($OutputPath)
if ([BitConverter]::ToUInt16($bytes, 0) -ne 0) { throw 'ICONDIR reserved is not 0' }
if ([BitConverter]::ToUInt16($bytes, 2) -ne 1) { throw 'ICONDIR type is not 1 (icon)' }
$count = [BitConverter]::ToUInt16($bytes, 4)
if ($count -ne $Specs.Count) { throw "ICONDIR count is $count, expected $($Specs.Count)" }

$ranges = @()
for ($i = 0; $i -lt $count; $i++) {
    $e = 6 + (16 * $i)
    $len = [BitConverter]::ToUInt32($bytes, $e + 8)
    $off = [BitConverter]::ToUInt32($bytes, $e + 12)
    if (($off + $len) -gt $bytes.Length) { throw "entry $i runs past end of file" }
    foreach ($r in $ranges) {
        if ($off -lt ($r.Off + $r.Len) -and $r.Off -lt ($off + $len)) { throw "entry $i overlaps another payload" }
    }
    $ranges += [pscustomobject]@{ Off = $off; Len = $len }
}
Write-Host "  container structure OK (no overlaps, all in bounds)"

# The 256 payload really is a decodable 256x256 PNG.
$last = $ranges[$ranges.Count - 1]
$png = New-Object byte[] $last.Len
[Array]::Copy($bytes, $last.Off, $png, 0, $last.Len)
$sig = [byte[]] @(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
for ($i = 0; $i -lt $sig.Length; $i++) {
    if ($png[$i] -ne $sig[$i]) { throw '256 entry is not a PNG' }
}
$ms = New-Object System.IO.MemoryStream (, $png)
try {
    $img = [System.Drawing.Image]::FromStream($ms)
    try {
        if ($img.Width -ne 256 -or $img.Height -ne 256) { throw "256 entry decoded to $($img.Width)x$($img.Height)" }
        Write-Host "  256px entry is a valid 256x256 PNG"
    } finally { $img.Dispose() }
} finally { $ms.Dispose() }
Write-Host "Done."
