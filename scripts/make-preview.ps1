# Renders assets/preview.png for the README: the tray spark states + example taskbar pills,
# using the same colors/spark shape the app draws. Not a desktop screenshot (privacy-safe) — a
# faithful render of the UI. ponytail: duplicates the ~10-line spark formula from Program.cs so
# doc tooling stays out of the app; if the spark ever changes, re-run this.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$W = 1200; $H = 470
$bg     = [System.Drawing.Color]::FromArgb(30, 30, 30)
$card   = [System.Drawing.Color]::FromArgb(40, 40, 40)
$white  = [System.Drawing.Color]::FromArgb(235, 235, 235)
$grey   = [System.Drawing.Color]::FromArgb(150, 150, 150)
$mid = [char]0x00B7; $ell = [char]0x2026   # middot, ellipsis (avoid encoding surprises)

# state -> color, matching StateColor()/BrandColor() in Program.cs
function StateColor([string]$state, [string]$provider) {
    switch ($state) {
        'permission' { return [System.Drawing.Color]::Gold }
        'done'       { return [System.Drawing.Color]::MediumSeaGreen }
        { $_ -in 'thinking','tool' } {
            if ($provider -eq 'Antigravity') { return [System.Drawing.Color]::FromArgb(66,133,244) }
            return [System.Drawing.Color]::DarkOrange
        }
        default { return [System.Drawing.Color]::SlateGray }
    }
}

$bmp = New-Object System.Drawing.Bitmap $W, $H
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.TextRenderingHint = 'ClearTypeGridFit'
$g.Clear($bg)

function RoundRect($gr, $x, $y, $w, $h, $r, [System.Drawing.Color]$fill) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, $r, $r, 180, 90); $p.AddArc($x+$w-$r, $y, $r, $r, 270, 90)
    $p.AddArc($x+$w-$r, $y+$h-$r, $r, $r, 0, 90); $p.AddArc($x, $y+$h-$r, $r, $r, 90, 90)
    $p.CloseFigure()
    $b = New-Object System.Drawing.SolidBrush $fill; $gr.FillPath($b, $p); $b.Dispose(); $p.Dispose()
}

# 8-ray spark, matching DrawSpark()
function Spark($gr, $cx, $cy, $rInner, $rOuter, [System.Drawing.Color]$c, $thick) {
    $pen = New-Object System.Drawing.Pen $c, $thick
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    for ($i = 0; $i -lt 8; $i++) {
        $a = [Math]::PI * 2 * $i / 8
        $dx = [Math]::Cos($a); $dy = [Math]::Sin($a)
        $gr.DrawLine($pen, $cx + $dx*$rInner, $cy + $dy*$rInner, $cx + $dx*$rOuter, $cy + $dy*$rOuter)
    }
    $pen.Dispose()
}

$fTitle = New-Object System.Drawing.Font 'Segoe UI', 30, ([System.Drawing.FontStyle]::Bold)
$fSub   = New-Object System.Drawing.Font 'Segoe UI', 14
$fLabel = New-Object System.Drawing.Font 'Segoe UI', 12
$fPill  = New-Object System.Drawing.Font 'Segoe UI', 13
$wBrush = New-Object System.Drawing.SolidBrush $white
$gBrush = New-Object System.Drawing.SolidBrush $grey

$g.DrawString('ClaudeStatusTray', $fTitle, $wBrush, 48, 34)
$g.DrawString("Live agent status in your Windows tray $mid taskbar", $fSub, $gBrush, 52, 92)

# --- tray icon states ---
$g.DrawString('TRAY ICON', $fLabel, $gBrush, 52, 148)
$states = @(
    @{ s='idle';       p='Claude';      t='Idle' },
    @{ s='thinking';   p='Claude';      t='Thinking' },
    @{ s='tool';       p='Claude';      t='Running tool' },
    @{ s='permission'; p='Claude';      t='Permission' },
    @{ s='done';       p='Claude';      t='Done' }
)
$x = 90
foreach ($st in $states) {
    $col = StateColor $st.s $st.p
    Spark $g ($x) 205 6 18 $col 3.2
    if ($st.s -eq 'permission') {
        $bb = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::OrangeRed)
        $g.FillEllipse($bb, $x+9, 186, 11, 11); $bb.Dispose()
    }
    $sz = $g.MeasureString($st.t, $fLabel)
    $g.DrawString($st.t, $fLabel, $gBrush, $x - $sz.Width/2, 232)
    $x += 220
}

# --- example taskbar pills ---
$g.DrawString('TASKBAR PILL', $fLabel, $gBrush, 52, 300)
$pills = @(
    @{ text="[C] my-project $mid Editing $mid 1m 4s";   s='tool';       p='Claude' },
    @{ text="[A] webapp $mid Reasoning$ell $mid 0m 8s"; s='thinking';   p='Antigravity' },
    @{ text="[C] api $mid Awaiting permission";          s='permission'; p='Claude' },
    @{ text="[A] demo $mid Done";                        s='done';       p='Antigravity' }
)
$px = @(48, 630); $py = @(340, 400)
for ($i = 0; $i -lt 4; $i++) {
    $col = New-Object System.Drawing.Font 'Segoe UI', 13
    $pill = $pills[$i]
    $tx = $px[$i % 2]; $ty = $py[[Math]::Floor($i / 2)]
    $tw = [int]($g.MeasureString($pill.text, $fPill).Width) + 46
    RoundRect $g $tx $ty $tw 40 12 $card
    Spark $g ($tx + 22) ($ty + 20) 4 11 (StateColor $pill.s $pill.p) 2.4
    $g.DrawString($pill.text, $fPill, $wBrush, $tx + 40, $ty + 10)
}

$outDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'assets'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$out = Join-Path $outDir 'preview.png'
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "Wrote $out"
