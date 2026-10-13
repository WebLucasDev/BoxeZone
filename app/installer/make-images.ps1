<#
.SYNOPSIS
  Renders the installer wizard images from the app logo with headless Edge: the large side image
  (dark panel + glove) and the small corner image (the logo), one file per DPI size, into images\.
  Run it again after changing Assets\BoxeZone.svg or BoxeZone-small.svg.
.EXAMPLE
  .\app\installer\make-images.ps1
#>
param([string]$OutDir = (Join-Path $PSScriptRoot 'images'))
Add-Type -AssemblyName System.Drawing
$assets = Join-Path (Split-Path $PSScriptRoot -Parent) 'Assets'
$full = [IO.File]::ReadAllText("$assets\BoxeZone.svg")
$small = [IO.File]::ReadAllText("$assets\BoxeZone-small.svg")
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
New-Item -ItemType Directory -Force $OutDir | Out-Null

$large = @(@(202, 386), @(269, 515), @(336, 643), @(403, 772), @(430, 824), @(498, 953), @(534, 1022))
$smallSizes = 58, 77, 97, 116, 124, 143, 159

# Everything on one page, side by side, then cropped (Edge cannot screenshot tiny windows).
$x = 0; $items = @(); $html = ''
foreach ($s in $large) {
    $w = $s[0]; $h = $s[1]; $logo = [int]($w * 0.56)
    $html += "<div class='large' style='left:${x}px;width:${w}px;height:${h}px'><div style='width:${logo}px;height:${logo}px;margin-top:$([int]($h * 0.40 - $logo / 2))px'>$full</div></div>"
    $items += [pscustomobject]@{ Name = "wizard-large-$w.png"; X = $x; W = $w; H = $h }
    $x += $w + 10
}
foreach ($n in $smallSizes) {
    $svg = if ($n -lt 77) { $small } else { $full }
    # Transparent margin: the image area touches the window's top and right edges.
    $pad = [int]($n * 0.14)
    $html += "<div class='small' style='left:${x}px;width:${n}px;height:${n}px;box-sizing:border-box;padding:${pad}px'>$svg</div>"
    $items += [pscustomobject]@{ Name = "wizard-small-$n.png"; X = $x; W = $n; H = $n }
    $x += $n + 10
}
$page = @"
<!doctype html><html><head><style>
html,body{margin:0;background:transparent}
.large,.small{position:absolute;top:0}
.large{background:radial-gradient(120% 60% at 50% 40%,#1b2a35 0%,#11161c 55%,#0b0e12 100%);display:flex;justify-content:center}
svg{width:100%;height:100%;display:block}
</style></head><body>$html</body></html>
"@
$htmlFile = Join-Path $OutDir '_page.html'; [IO.File]::WriteAllText($htmlFile, $page)
$png = Join-Path $OutDir '_page.png'
& $edge --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 --default-background-color=00000000 "--window-size=$($x),1030" "--screenshot=$png" "file:///$($htmlFile -replace '\\','/')" 2>&1 | Out-Null
Start-Sleep -Seconds 2
$bmp = [System.Drawing.Bitmap]::FromFile($png)
foreach ($it in $items) {
    $crop = $bmp.Clone((New-Object System.Drawing.Rectangle($it.X, 0, $it.W, $it.H)), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $crop.Save((Join-Path $OutDir $it.Name), [System.Drawing.Imaging.ImageFormat]::Png); $crop.Dispose()
}
$bmp.Dispose()
Remove-Item $htmlFile, $png
Get-ChildItem $OutDir | ForEach-Object { "$($_.Name) $($_.Length)" }
