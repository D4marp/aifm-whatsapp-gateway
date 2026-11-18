# Builds POC-REPORT-MAA-2102.html (and .pdf with Edge) from the .md plus the figures in DESAIN-SISTEM-MAA-2102.html.
# Run from the repo root:  powershell -ExecutionPolicy Bypass -File scripts\build-poc-report.ps1
# Indonesian version: -Md POC-REPORT-MAA-2102.id.md -Design DESAIN-SISTEM-MAA-2102.id.html -Out POC-REPORT-MAA-2102.id
param([string]$Md = "POC-REPORT-MAA-2102.md", [string]$Design = "DESAIN-SISTEM-MAA-2102.html", [string]$Out = "POC-REPORT-MAA-2102")
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$md = Get-Content -Raw -Encoding UTF8 (Join-Path $root $Md)
$design = Get-Content -Raw -Encoding UTF8 (Join-Path $root $Design)
$svgs = @([regex]::Matches($design, '(?s)<svg .*?</svg>') | ForEach-Object { $_.Value })

function E($s) { $s = [System.Net.WebUtility]::HtmlEncode($s); [regex]::Replace($s, '`([^`]+)`', '<code>$1</code>') }

$o = New-Object System.Text.StringBuilder
$inT = $false; $inCode = $false; $inList = $false
foreach ($l in ($md -split "`r?`n")) {
  # fenced code block: kept as written
  if ($l -match '^```') {
    if ($inCode) { [void]$o.Append('</pre>'); $inCode = $false } else { [void]$o.Append('<pre>'); $inCode = $true }
    continue
  }
  if ($inCode) { [void]$o.Append([System.Net.WebUtility]::HtmlEncode($l) + "`n"); continue }

  if ($l -match '^\|') {
    if ($l -match '^\|[-| ]+\|$') { continue }
    # "\|" inside a cell is a literal pipe, not a column break
    $sep = [string][char]1
    $cells = $l.Replace('\|', $sep).Trim('|') -split '\|' | ForEach-Object { $_.Trim().Replace($sep, '|') }
    if (-not $inT) { [void]$o.Append('<table>'); $inT = $true; $tag = 'th' } else { $tag = 'td' }
    [void]$o.Append('<tr>' + (($cells | ForEach-Object { "<$tag>" + (E $_) + "</$tag>" }) -join '') + '</tr>')
    continue
  }
  if ($inT) { [void]$o.Append('</table>'); $inT = $false }

  if ($l -match '^- (.*)') {
    if (-not $inList) { [void]$o.Append('<ul>'); $inList = $true }
    [void]$o.Append('<li>' + (E $Matches[1]) + '</li>')
    continue
  }
  if ($inList) { [void]$o.Append('</ul>'); $inList = $false }

  if ($l -match '^# (.*)') { [void]$o.Append('<h1>' + (E $Matches[1]) + '</h1>') }
  elseif ($l -match '^## (.*)') { [void]$o.Append('<h2>' + (E $Matches[1]) + '</h2>') }
  elseif ($l -match '^### (.*)') { [void]$o.Append('<h3>' + (E $Matches[1]) + '</h3>') }
  elseif ($l -match '^!\[fig:(\d)\]') { [void]$o.Append('<div class="fig f' + $Matches[1] + '">' + $svgs[[int]$Matches[1] - 1] + '</div>') }
  elseif ($l -match '^!\[img:(.*)\]') { [void]$o.Append('<div class="fig"><img src="' + $Matches[1] + '"></div>') }
  elseif ($l.Trim() -ne '') { [void]$o.Append('<p>' + (E $l) + '</p>') }
}
if ($inT) { [void]$o.Append('</table>') }
if ($inList) { [void]$o.Append('</ul>') }
if ($inCode) { [void]$o.Append('</pre>') }

$css = @'
body{font-family:Segoe UI,Arial,sans-serif;font-size:11pt;color:#111;margin:0}
h1{font-size:18pt;margin:0 0 6pt}h3{font-size:11.5pt;margin:10pt 0 4pt;break-after:avoid}pre{font-family:Consolas,monospace;font-size:9pt;background:#f4f4f4;border:1px solid #ccc;padding:6pt;white-space:pre-wrap;word-break:break-all;margin:4pt 0}ul{margin:4pt 0 4pt 18pt;padding:0}li{margin:2pt 0}h2{font-size:13pt;margin:16pt 0 6pt;break-after:avoid}
p{margin:4pt 0;break-after:avoid}
table{width:100%;border-collapse:collapse;table-layout:fixed;margin:6pt 0}
th,td{border:1px solid #444;padding:4pt 6pt;text-align:left;vertical-align:top;word-wrap:break-word;font-size:10pt}
code{font-size:9pt}tr{page-break-inside:avoid}
.fig{margin:6pt 0;text-align:center;break-inside:avoid}
.fig svg{width:100%}.fig.f1 svg{width:78%}.fig.f2 svg{width:66%}.fig img{width:74%;border:1px solid #444}
text{font-family:Segoe UI,Arial,sans-serif;fill:#111}
.t{font-size:14px;font-weight:bold}.s{font-size:12.5px}.h{font-size:15px;font-weight:bold}.n{font-size:12px;font-style:italic}
'@
$html = "<!doctype html><html><head><meta charset='utf-8'><title>MAA-2102 POC report</title><style>@page{size:A4;margin:16mm}$css</style></head><body>" + $o.ToString() + "</body></html>"
[IO.File]::WriteAllText("$root\$Out.html", $html, (New-Object Text.UTF8Encoding $false))

$edge = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
if (-not (Test-Path $edge)) { $edge = "C:\Program Files\Microsoft\Edge\Application\msedge.exe" }
$uri = "file:///" + ($root -replace '\\', '/') + "/$Out.html"
& $edge --headless --disable-gpu --no-pdf-header-footer --print-to-pdf="$root\$Out.pdf" $uri 2>$null | Out-Null
"done"
