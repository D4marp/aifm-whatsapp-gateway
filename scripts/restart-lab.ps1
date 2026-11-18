# Restarts the lab API safely: disconnects every number first (keeps the logins), stops the process, builds Release, starts it again.
# A forced stop without Disconnect lost a login on 16 Nov, so always use this instead of killing the process.
# Usage (from the repo root):  powershell -ExecutionPolicy Bypass -File scripts\restart-lab.ps1
param([int]$Port = 5300, [switch]$NoBuild)
$root = Split-Path -Parent $PSScriptRoot
$base = "http://localhost:$Port/api/whatsapp"

try {
    $sessions = Invoke-RestMethod "$base/sessions" -TimeoutSec 10
    foreach ($s in @($sessions)) {
        try { Invoke-RestMethod -Method Post "$base/connection/disconnect?sessionId=$($s.id)" -TimeoutSec 60 | Out-Null; "disconnected: $($s.name)" } catch { "disconnect failed for $($s.name): $($_.Exception.Message)" }
    }
} catch { "API not running on port $Port (nothing to disconnect)" }
Start-Sleep 3

$c = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
if ($c) { Stop-Process -Id $c.OwningProcess -Force; "stopped process $($c.OwningProcess)" }
Start-Sleep 3

Set-Location $root
if (-not $NoBuild) {
    $out = dotnet build src\WhatsAppLab.Api -c Release 2>&1
    if ($LASTEXITCODE -ne 0) { $out | Select-String " error " | Select-Object -First 5; throw "Build failed; the API was NOT started." }
    "build ok"
}
Start-Process -FilePath dotnet -ArgumentList "run", "--project", "src\WhatsAppLab.Api", "-c", "Release", "--no-build", "--urls", "http://localhost:$Port" `
    -WindowStyle Hidden -RedirectStandardOutput "$env:TEMP\lab-run.txt" -RedirectStandardError "$env:TEMP\lab-run-err.txt"
"started on http://localhost:$Port (log: $env:TEMP\lab-run.txt)"
