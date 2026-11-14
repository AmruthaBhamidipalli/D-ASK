# Test fixture (2026-11-15): a SCANNED, image-only PDF (no text layer), like a photocopied paper. Each page is text drawn into a
# bitmap with System.Drawing, then wrapped as a /DeviceGray /FlateDecode image XObject by make-scanned-pdf.mjs.
#   powershell -File make-scanned-pdf.ps1 -Out <file.pdf>
param([Parameter(Mandatory = $true)][string]$Out)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$pages = @(
    @('Scanned Field Notes', 'Page one: the sediment cores were collected', 'at Lake Varna on 12 March 2024.', 'Mean core length was 41 centimetres.'),
    @('Results', 'Page two: pollen counts doubled after 1950.', 'Charcoal peaks match recorded fires.', 'The lake level fell by 1.8 metres.')
)
$dir = Split-Path -Parent ([IO.Path]::GetFullPath($Out))
$bmps = @()
for ($p = 0; $p -lt $pages.Count; $p++) {
    $bmp = New-Object System.Drawing.Bitmap(1275, 1650)   # US Letter at 150 dpi
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::White)
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $title = New-Object System.Drawing.Font('Times New Roman', 34, [System.Drawing.FontStyle]::Bold)
    $body = New-Object System.Drawing.Font('Times New Roman', 24)
    $g.DrawString($pages[$p][0], $title, [System.Drawing.Brushes]::Black, 120, 140)
    for ($l = 1; $l -lt $pages[$p].Count; $l++) { $g.DrawString($pages[$p][$l], $body, [System.Drawing.Brushes]::Black, 120, 230 + 60 * $l) }
    $file = Join-Path $dir ("scan-page-{0}.bmp" -f ($p + 1))
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Bmp)
    $g.Dispose(); $bmp.Dispose()
    $bmps += $file
}
node (Join-Path $PSScriptRoot 'make-scanned-pdf.mjs') $Out @bmps
foreach ($b in $bmps) { Remove-Item $b -Force }
