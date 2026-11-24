# Draws a fake Notepad "save changes?" dialog for the vision test (no real screen capture).
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot 'dialog.png'
$bmp = New-Object System.Drawing.Bitmap 520, 220
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.TextRenderingHint = 'ClearTypeGridFit'
$g.Clear([System.Drawing.Color]::White)

# Title bar
$g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(240,240,240))), 0, 0, 520, 34)
$titleFont = New-Object System.Drawing.Font 'Segoe UI', 10
$g.DrawString('Notepad', $titleFont, [System.Drawing.Brushes]::Black, 10, 8)
$g.DrawString('X', $titleFont, [System.Drawing.Brushes]::Black, 494, 8)

# Message
$msgFont = New-Object System.Drawing.Font 'Segoe UI', 13
$g.DrawString('Do you want to save changes to Untitled?', $msgFont, [System.Drawing.Brushes]::DarkBlue, 24, 70)

# Buttons
$btnFont = New-Object System.Drawing.Font 'Segoe UI', 10
$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(120,120,120)), 1
$x = 190
foreach ($label in 'Save', "Don't Save", 'Cancel') {
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(225,225,225))), $x, 160, 100, 32)
    $g.DrawRectangle($pen, $x, 160, 100, 32)
    $size = $g.MeasureString($label, $btnFont)
    $g.DrawString($label, $btnFont, [System.Drawing.Brushes]::Black, ($x + (100 - $size.Width) / 2), 166)
    $x += 108
}
$g.Dispose()
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"Saved $out"
