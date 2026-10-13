# Offline caliber measurement for a baked surface patch PNG.
#   1) Ring band (r in [0.965, 0.995]*R) = the clear color = background-biome average color
#      -> this is the BASIN/OCEAN caliber that the base sphere approximates.
#   2) Mode color inside the disk (r <= 0.90*R) = the baked ocean.
#   3) Ratio of the two luminances: ~1 means the calibers agree; far off means a color-space bug.
# NOTE: ASCII only - Windows PowerShell reads .ps1 as ANSI/GBK, non-ASCII would be mangled.
# Usage: powershell -ExecutionPolicy Bypass -File tools/measure-bake-calibers.ps1 -Path <png>
param([Parameter(Mandatory=$true)][string]$Path)

Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile((Resolve-Path $Path))
$w = $bmp.Width; $h = $bmp.Height
Write-Host ("image: {0}  {1}x{2}" -f (Split-Path $Path -Leaf), $w, $h)

$rect = New-Object System.Drawing.Rectangle 0,0,$w,$h
$data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$bytes = New-Object byte[] ($data.Stride * $h)
[System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
$bmp.UnlockBits($data); $bmp.Dispose()

$cx = $w / 2.0; $cy = $h / 2.0
$R  = $w / 2.0

$ringR = 0.0; $ringG = 0.0; $ringB = 0.0; $ringN = 0
$inR = 0.0; $inG = 0.0; $inB = 0.0; $inN = 0
$hist = @{}
$step = 8
for ($y = 0; $y -lt $h; $y += $step) {
    $dy = $y - $cy
    for ($x = 0; $x -lt $w; $x += $step) {
        $dx = $x - $cx
        $r = [Math]::Sqrt($dx*$dx + $dy*$dy) / $R
        $i = $y * $data.Stride + $x * 4
        $b = $bytes[$i]; $g = $bytes[$i+1]; $rr = $bytes[$i+2]
        if ($r -ge 0.965 -and $r -le 0.995) { $ringR += $rr; $ringG += $g; $ringB += $b; $ringN++ }
        elseif ($r -le 0.90) {
            $inR += $rr; $inG += $g; $inB += $b; $inN++
            $k = ("{0},{1},{2}" -f [int]($rr/16), [int]($g/16), [int]($b/16))
            if ($hist.ContainsKey($k)) { $hist[$k]++ } else { $hist[$k] = 1 }
        }
    }
}

if ($ringN -gt 0) {
    Write-Host ("1) ring band (clear color = bg biome average): ({0:F0},{1:F0},{2:F0})  n={3}" -f ($ringR/$ringN),($ringG/$ringN),($ringB/$ringN),$ringN)
} else { Write-Host "1) ring band: no samples" }
if ($inN -gt 0) {
    Write-Host ("2) disk average: ({0:F0},{1:F0},{2:F0})  n={3}" -f ($inR/$inN),($inG/$inN),($inB/$inN),$inN)
}

# radial profile: mean color per 0.05R band - shows where terrain ends and what the "clear color" really is
$prof = @{}
for ($y = 0; $y -lt $h; $y += $step) {
    $dy = $y - $cy
    for ($x = 0; $x -lt $w; $x += $step) {
        $dx = $x - $cx
        $r = [Math]::Sqrt($dx*$dx + $dy*$dy) / $R
        $i = $y * $data.Stride + $x * 4
        $k = [int][Math]::Floor($r * 20)   # 0.05R bins
        if (-not $prof.ContainsKey($k)) { $prof[$k] = @(0.0,0.0,0.0,0) }
        $e = $prof[$k]
        $prof[$k] = @(($e[0]+$bytes[$i+2]), ($e[1]+$bytes[$i+1]), ($e[2]+$bytes[$i]), ($e[3]+1))
    }
}
Write-Host "1b) radial profile (mean RGB per 0.05R band; r=1.0 is the inscribed disk edge):"
foreach ($k in ($prof.Keys | Sort-Object)) {
    $e = $prof[$k]
    if ($e[3] -le 0) { continue }
    Write-Host ("     r {0:F2}-{1:F2}  ({2,3:F0},{3,3:F0},{4,3:F0})  n={5}" -f ($k*0.05), (($k+1)*0.05), ($e[0]/$e[3]), ($e[1]/$e[3]), ($e[2]/$e[3]), $e[3])
}

$top = $hist.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 5
Write-Host "2b) top-5 mode colors inside the disk (16-step buckets, bucket midpoint):"
foreach ($e in $top) {
    $p = $e.Key.Split(',')
    $mr = [int]$p[0]*16+8; $mg = [int]$p[1]*16+8; $mb = [int]$p[2]*16+8
    $pct = 100.0 * $e.Value / [Math]::Max(1,$inN)
    Write-Host ("     ({0},{1},{2})  {3:F1}%" -f $mr,$mg,$mb,$pct)
}

if ($ringN -gt 0 -and $inN -gt 0 -and $top.Count -gt 0) {
    $p = $top[0].Key.Split(',')
    $mr = [int]$p[0]*16+8; $mg = [int]$p[1]*16+8; $mb = [int]$p[2]*16+8
    $lumIn = ($mr + $mg + $mb) / 3.0
    $lumR  = ($ringR/$ringN + $ringG/$ringN + $ringB/$ringN) / 3.0
    $ratio = if ($lumR -gt 1) { $lumIn / $lumR } else { 0 }
    Write-Host ("3) disk mode luminance {0:F1} / ring luminance {1:F1} = **{2:F2}**   (near 1 = same caliber; <0.7 or >1.4 = mismatch)" -f $lumIn,$lumR,$ratio)
}
