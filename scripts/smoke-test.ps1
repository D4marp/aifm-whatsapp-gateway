# Proves the whole lab end to end with the Fake WhatsApp client. Touches only the database WhatsAppLab (it is dropped and rebuilt).
# Usage: powershell -ExecutionPolicy Bypass -File scripts\smoke-test.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$port = 5310; $base = "http://localhost:$port/api/whatsapp"
$results = New-Object System.Collections.Generic.List[object]
function Check($name, $expect, $actual, [bool]$ok) { $results.Add([pscustomobject]@{ Check = $name; Expected = $expect; Actual = $actual; Result = $(if ($ok) { 'PASS' } else { 'FAIL' }) }); Write-Host ("{0} | expected: {1} | actual: {2} | {3}" -f $name, $expect, $actual, $(if ($ok) { 'PASS' } else { 'FAIL' })) }
function Sql($q) { & sqlcmd -S localhost -E -I -C -b -Q $q | Out-Null; if ($LASTEXITCODE -ne 0) { throw "sql failed" } }
function Arr($uri) { Invoke-RestMethod $uri | ForEach-Object { $_ } }   # Windows PowerShell 5.1 does not unroll a JSON array by itself
function Msgs { Arr "$base/messages" }
function WaitFor([scriptblock]$cond, [int]$seconds) { for ($i = 0; $i -lt $seconds; $i++) { if (& $cond) { return $true }; Start-Sleep 1 }; return $false }
function Body($b) { $b | ConvertTo-Json -Compress }
function Put($path, $obj) { Invoke-RestMethod -Method Put -Uri "$base$path" -ContentType 'application/json' -Body (Body $obj) }
function Post($path, $obj) { Invoke-RestMethod -Method Post -Uri "$base$path" -ContentType 'application/json' -Body (Body $obj) }
function StatusOf($text) { @(Msgs | Where-Object { $_.body -eq $text } | Select-Object -First 1)[0] }

Sql "IF DB_ID('WhatsAppLab') IS NOT NULL BEGIN ALTER DATABASE WhatsAppLab SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE WhatsAppLab; END"
$env:ASPNETCORE_URLS = "http://localhost:$port"
# The app now defaults to the REAL client and the real database. This test must never touch real WhatsApp, so it forces the Fake client and its own database.
$env:WhatsApp__Client = 'Fake'; $env:WhatsApp__AutoConnect = 'false'
$env:ConnectionStrings__DefaultConnection = 'Server=localhost;Database=WhatsAppLab;Trusted_Connection=true;Encrypt=true;TrustServerCertificate=true;MultipleActiveResultSets=true'
$dll = Get-ChildItem "$root\src\WhatsAppLab.Api\bin" -Recurse -Filter WhatsAppLab.Api.dll | Select-Object -First 1
$p = Start-Process dotnet -ArgumentList $dll.Name -WorkingDirectory $dll.DirectoryName -PassThru -WindowStyle Hidden -RedirectStandardOutput "$root\scripts\api-out.txt" -RedirectStandardError "$root\scripts\api-err.txt"
try {
    if (-not (WaitFor { try { $null = Invoke-RestMethod "$base/status"; $true } catch { $false } } 90)) { throw 'API did not start' }
    $itJid = '120363000000000001@g.us'

    $s = Invoke-RestMethod "$base/status"
    Check 'Start: feature is Off by default (no settings row)' 'enabled = false' "enabled = $($s.enabled), client = $($s.client)" (-not $s.enabled)

    $g = Post '/groups/sync' @{}
    $groups = @(Arr "$base/groups")
    Check 'Groups: sync copies the groups of the linked number' '2 groups, IT Batam with 12 members' "$($g.synced) groups; IT Batam = $(($groups | Where-Object name -eq 'IT Batam').participantCount) members" ($g.synced -eq 2 -and ($groups | Where-Object name -eq 'IT Batam').participantCount -eq 12)

    $null = Post '/messages' @{ groupName = 'IT Batam'; body = 'T2 while off' }
    Start-Sleep 6
    $m = StatusOf 'T2 while off'
    Check 'Off: a queued message is NOT sent' 'status 0 (Pending)' "status $($m.status)" ($m.status -eq 0)

    $null = Put '/settings' @{ isEnabled = $true; maxMessagesPerDay = 100; minSecondsBetweenMessages = 1; receiveEnabled = $true }
    $ok = WaitFor { (StatusOf 'T2 while off').status -eq 1 } 15
    Check 'Switch on: the waiting message is sent' 'status 1 (Sent)' "status $((StatusOf 'T2 while off').status)" $ok

    $null = Post '/messages' @{ groupName = 'IT Batam'; body = 'T4 from the API' }
    $ok = WaitFor { (StatusOf 'T4 from the API').status -eq 1 } 15
    $sentList = @(Arr "$base/fake/sent")
    Check 'Trigger API: message is sent to the group id' 'Sent, to the IT Batam id' "status $((StatusOf 'T4 from the API').status); fake client got it: $([bool](@($sentList | Where-Object { $_.body -eq 'T4 from the API' -and $_.jid -eq $itJid }).Count))" ($ok -and @($sentList | Where-Object { $_.body -eq 'T4 from the API' -and $_.jid -eq $itJid }).Count -eq 1)

    Sql "INSERT INTO WhatsAppLab.dbo.WhatsAppOutboundMessageDbSet (Id, GroupJid, Body, ScheduledFor, Status, Attempts, Source, SourceReference, CorrelationId, IsDeleted, CreatedBy, CreatedOn) VALUES (NEWID(), '$itJid', 'T5 from SQL Server', SYSUTCDATETIME(), 0, 0, 1, 'sql-demo', 'sql1', 0, 'sql', SYSUTCDATETIME())"
    $ok = WaitFor { (StatusOf 'T5 from SQL Server').status -eq 1 } 15
    Check 'Trigger SQL Server: a row inserted by SQL is sent' 'Sent' "status $((StatusOf 'T5 from SQL Server').status)" $ok

    $when = (Get-Date).ToUniversalTime().AddSeconds(8).ToString('yyyy-MM-ddTHH:mm:ssZ')
    $null = Post '/messages' @{ groupName = 'IT Batam'; body = 'T6 scheduled'; scheduledFor = $when }
    Start-Sleep 2
    $early = (StatusOf 'T6 scheduled').status
    $ok = WaitFor { (StatusOf 'T6 scheduled').status -eq 1 } 20
    Check 'Scheduled time: not sent early, sent when due' 'Pending first, then Sent' "first $early, then $((StatusOf 'T6 scheduled').status)" ($early -eq 0 -and $ok)

    $null = Post '/announcements' @{ id = 'daily-test'; groupName = 'IT Batam'; body = 'T7 announcement'; cron = '0 9 * * *' }
    Start-Sleep 2; $null = Invoke-RestMethod -Method Post "$base/announcements/daily-test/run-now"
    $ok = WaitFor { (StatusOf 'T7 announcement').status -eq 1 } 30
    $row = StatusOf 'T7 announcement'
    Check 'Schedule (Hangfire recurring job): the job fills the outbox, then it is sent' 'Source 2 (Scheduled), Sent' "source $($row.source), status $($row.status)" ($ok -and $row.source -eq 2)

    $null = Invoke-RestMethod -Method Post "$base/fake/fail-next"
    $null = Post '/messages' @{ groupName = 'IT Batam'; body = 'T8 retry' }
    Start-Sleep 5
    $first = StatusOf 'T8 retry'
    $ok = WaitFor { (StatusOf 'T8 retry').status -eq 1 } 30
    Check 'Failure: the first send fails, it is retried and then sent' 'Attempts 1 with an error, then Sent' "after first try: attempts $($first.attempts), error '$($first.errorMessage)'; final status $((StatusOf 'T8 retry').status), attempts $((StatusOf 'T8 retry').attempts)" ($first.attempts -ge 1 -and $first.errorMessage -and $ok)

    $st = Invoke-RestMethod "$base/status"
    $null = Put '/settings' @{ isEnabled = $true; maxMessagesPerDay = $st.sent; minSecondsBetweenMessages = 1; receiveEnabled = $true }
    $null = Post '/messages' @{ groupName = 'IT Batam'; body = 'T9 over the daily cap' }
    Start-Sleep 6
    $capped = (StatusOf 'T9 over the daily cap').status
    $null = Put '/settings' @{ isEnabled = $true; maxMessagesPerDay = $st.sent + 50; minSecondsBetweenMessages = 1; receiveEnabled = $true }
    $ok = WaitFor { (StatusOf 'T9 over the daily cap').status -eq 1 } 15
    Check 'Daily cap: over the cap it waits, after the cap is raised it is sent' 'Pending, then Sent' "while capped $capped, then $((StatusOf 'T9 over the daily cap').status)" ($capped -eq 0 -and $ok)

    $null = Post '/fake/incoming' @{ groupName = 'IT Batam'; sender = 'Karen'; body = 'T10 hello from a member' }
    Start-Sleep 2
    $in = @(Arr "$base/inbound" | Where-Object { $_.body -eq 'T10 hello from a member' })
    Check 'Receive: an incoming group message is recorded and marked processed' '1 row, status 1 (Processed)' "$($in.Count) row, status $($in[0].processStatus)" ($in.Count -eq 1 -and $in[0].processStatus -eq 1)

    $null = Put '/settings' @{ isEnabled = $false; maxMessagesPerDay = 100; minSecondsBetweenMessages = 1; receiveEnabled = $true }
    $null = Post '/messages' @{ groupName = 'IT Batam'; body = 'T11 after off' }
    Start-Sleep 6
    Check 'Switch off again: nothing more is sent' 'Pending' "status $((StatusOf 'T11 after off').status)" ((StatusOf 'T11 after off').status -eq 0)

    $bad = try { Post '/messages' @{ groupName = 'No Such Group'; body = 'x' }; 'accepted' } catch { "refused ($([int]$_.Exception.Response.StatusCode))" }
    Check 'Unknown group name is refused' 'refused (400)' $bad ($bad -like 'refused (400)')

    # pacing: sending hours
    $h = (Get-Date).Hour
    $null = Put '/settings' @{ isEnabled = $true; maxMessagesPerDay = 100; minSecondsBetweenMessages = 1; receiveEnabled = $true; sendFromHour = (($h + 2) % 24); sendToHour = (($h + 3) % 24) }
    $null = Post '/messages' @{ groupName = 'IT Batam'; body = 'T12 outside the sending hours' }
    Start-Sleep 6
    $outside = (StatusOf 'T12 outside the sending hours').status
    $null = Put '/settings' @{ isEnabled = $true; maxMessagesPerDay = 100; minSecondsBetweenMessages = 1; receiveEnabled = $true; sendFromHour = $h; sendToHour = (($h + 1) % 24) }
    $ok = WaitFor { (StatusOf 'T12 outside the sending hours').status -eq 1 } 15
    Check 'Pacing hours: outside the sending hours it waits, inside it is sent' 'Pending, then Sent' "outside $outside, then $((StatusOf 'T12 outside the sending hours').status)" ($outside -eq 0 -and $ok)

    # pacing: wave limit (at most N per window)
    $st = Invoke-RestMethod "$base/status"
    $null = Put '/settings' @{ isEnabled = $true; maxMessagesPerDay = 100; minSecondsBetweenMessages = 1; receiveEnabled = $true; maxPerWindow = $st.sent; windowMinutes = 60 }
    $null = Post '/messages' @{ groupName = 'IT Batam'; body = 'T13 wave is full' }
    Start-Sleep 6
    $full = (StatusOf 'T13 wave is full').status
    $null = Put '/settings' @{ isEnabled = $true; maxMessagesPerDay = 100; minSecondsBetweenMessages = 1; receiveEnabled = $true; maxPerWindow = $st.sent + 5; windowMinutes = 60 }
    $ok = WaitFor { (StatusOf 'T13 wave is full').status -eq 1 } 15
    Check 'Pacing wave: when the wave is full it waits, when there is room it is sent' 'Pending, then Sent' "full $full, then $((StatusOf 'T13 wave is full').status)" ($full -eq 0 -and $ok)

    # pacing: random extra gap (messages sent one after another are at least the minimum apart)
    $null = Put '/settings' @{ isEnabled = $true; maxMessagesPerDay = 100; minSecondsBetweenMessages = 1; receiveEnabled = $true; jitterSeconds = 4 }
    $null = Post '/messages' @{ groupName = 'IT Batam'; body = 'T14 jitter a' }
    $null = Post '/messages' @{ groupName = 'IT Batam'; body = 'T14 jitter b' }
    $ok = WaitFor { (StatusOf 'T14 jitter a').status -eq 1 -and (StatusOf 'T14 jitter b').status -eq 1 } 30
    $gap = if ($ok) { [math]::Round(([datetime](StatusOf 'T14 jitter b').sentOn - [datetime](StatusOf 'T14 jitter a').sentOn).TotalSeconds, 1) } else { -1 }
    Check 'Pacing jitter: two messages are both sent, with a gap of at least the minimum' 'both Sent, gap >= 1 s' "both sent: $ok, gap $gap s" ($ok -and [math]::Abs($gap) -ge 1)
}
finally {
    if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force }
    $pass = ($results | Where-Object Result -eq 'PASS').Count; $fail = ($results | Where-Object Result -eq 'FAIL').Count
    Write-Host "SUMMARY: $pass PASS, $fail FAIL of $($results.Count)"
    $results | Export-Csv "$root\scripts\smoke-results.csv" -NoTypeInformation -Encoding UTF8
}

