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
$mediaRoot = Join-Path $env:TEMP 'wa-smoke-media'; if (Test-Path $mediaRoot) { Remove-Item $mediaRoot -Recurse -Force }
$env:Media__Root = $mediaRoot; $env:Media__MaxMegabytes = '1'   # the smoke test keeps its files in its own folder and uses a 1 MB limit so the size check can be tried
$env:ConnectionStrings__DefaultConnection = 'Server=localhost;Database=WhatsAppLab;Trusted_Connection=true;Encrypt=true;TrustServerCertificate=true;MultipleActiveResultSets=true'
$dll = Get-ChildItem "$root\src\WhatsAppLab.Api\bin" -Recurse -Filter WhatsAppLab.Api.dll | Sort-Object LastWriteTime -Descending | Select-Object -First 1
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

    # ---- test message variation: tokens are filled in when the message is queued, so two messages from one template differ
    $null = Put '/settings' @{ isEnabled = $true; maxMessagesPerDay = 1000; minSecondsBetweenMessages = 1; receiveEnabled = $true; jitterSeconds = 1 }
    $tpl = 'V1 {Hello|Hi|Hey} at {time} on {date} ref {rand} {keep this}'
    $prev = Post '/messages/preview' @{ body = $tpl }
    $null = Post '/messages' @{ groupName = 'IT Batam'; body = $tpl }
    $null = Post '/messages' @{ groupName = 'IT Batam'; body = $tpl }
    $null = WaitFor { @(Msgs | Where-Object { $_.body -like 'V1 *' -and $_.status -eq 1 }).Count -ge 2 } 30
    $v = @(Msgs | Where-Object { $_.body -like 'V1 *' })
    $rendered = ($v.Count -eq 2) -and (@($v | Where-Object { $_.body -match '\{(Hello|time|date|rand)' }).Count -eq 0) -and (@($v | Where-Object { $_.body -match '^V1 (Hello|Hi|Hey) at \d\d:\d\d:\d\d on \d{4}-\d\d-\d\d ref \d{4} \{keep this\}$' }).Count -eq 2)
    Check 'Test text: tokens are filled in, unknown braces are kept' 'no {time}/{rand}/{a|b} left, "{keep this}" stays' "$(($v | ForEach-Object body) -join ' || ')" $rendered
    Check 'Test text: two messages from one template are not identical' 'texts differ' "same text: $($v.Count -eq 2 -and $v[0].body -eq $v[1].body); preview: $($prev.text)" ($v.Count -eq 2 -and $v[0].body -ne $v[1].body -and $prev.text -match '^V1 ')

    # ---- media: files are stored outside the database with type, size and hash; they are sent and received through the same adapter
    $tmp = Join-Path $env:TEMP 'wa-smoke-files'; New-Item -ItemType Directory -Force $tmp | Out-Null
    $wav = Join-Path $tmp 'tone.wav'; $mp4 = Join-Path $tmp 'clip.mp4'; $exe = Join-Path $tmp 'run.exe'; $big = Join-Path $tmp 'big.mp4'
    [IO.File]::WriteAllBytes($wav, (New-Object byte[] 4000)); $r = New-Object Random 7; $b = New-Object byte[] 200000; $r.NextBytes($b); [IO.File]::WriteAllBytes($mp4, $b)
    [IO.File]::WriteAllBytes($exe, (New-Object byte[] 100)); [IO.File]::WriteAllBytes($big, (New-Object byte[] 2000000))
    function Upload($path, $type) { $o = & curl.exe -s -w "`n%{http_code}" -F "file=@$path;type=$type" "$base/media"; $lines = @($o -split "`n"); [pscustomobject]@{ Code = [int]$lines[-1]; Json = (($lines[0..($lines.Count - 2)] -join "`n") | ConvertFrom-Json -ErrorAction SilentlyContinue) } }
    $shaWav = (Get-FileHash $wav -Algorithm SHA256).Hash.ToLower(); $shaMp4 = (Get-FileHash $mp4 -Algorithm SHA256).Hash.ToLower()

    $u1 = Upload $wav 'audio/wav'
    Check 'Media: an audio file is accepted, with type, size and hash' 'kind audio, size 4000, same SHA-256' "code $($u1.Code), kind $($u1.Json.kind), size $($u1.Json.size), hash match $($u1.Json.sha256 -eq $shaWav)" ($u1.Code -eq 200 -and $u1.Json.kind -eq 'audio' -and $u1.Json.size -eq 4000 -and $u1.Json.sha256 -eq $shaWav)
    $u2 = Upload $exe 'application/octet-stream'
    Check 'Media: a program file is refused' 'refused (400)' "code $($u2.Code): $($u2.Json.error)" ($u2.Code -eq 400)
    $u3 = Upload $big 'video/mp4'
    Check 'Media: a file over the size limit is refused' 'refused (400)' "code $($u3.Code): $($u3.Json.error)" ($u3.Code -eq 400)
    $u4 = Upload $mp4 'video/mp4'

    $null = Post '/messages' @{ groupName = 'IT Batam'; body = 'D4 audio {rand}'; mediaId = $u1.Json.id }
    $null = Post '/messages' @{ groupName = 'IT Batam'; body = ''; mediaId = $u4.Json.id }
    $ok = WaitFor { @(Arr "$base/fake/sent-media").Count -ge 2 } 20
    $fm = @(Arr "$base/fake/sent-media")
    $aud = @($fm | Where-Object { $_.mime -like 'audio/*' })[0]; $vid = @($fm | Where-Object { $_.mime -like 'video/*' })[0]
    Check 'Media: the client receives the same bytes that were uploaded (audio and video)' 'both sent, same hashes, caption filled in' "sent: $ok; audio hash match $($aud.sha256 -eq $shaWav), caption '$($aud.caption)'; video hash match $($vid.sha256 -eq $shaMp4)" ($ok -and $aud.sha256 -eq $shaWav -and $vid.sha256 -eq $shaMp4 -and $aud.caption -match '^D4 audio \d{4}$')
    $types = @(Msgs | Where-Object { $_.mediaId } | ForEach-Object { $_.messageType } | Sort-Object)
    Check 'Media: the outbox row records the kind of file' 'audio and video' ($types -join ', ') ($types -contains 'audio' -and $types -contains 'video')

    $bad2 = try { Post '/messages' @{ groupName = 'IT Batam'; body = '' }; 'accepted' } catch { "refused ($([int]$_.Exception.Response.StatusCode))" }
    Check 'Media: a message with no text and no file is refused' 'refused (400)' $bad2 ($bad2 -like 'refused (400)')

    $null = Post '/fake/incoming-media' @{ groupName = 'IT Batam'; sender = 'Karen'; caption = 'D6 voice from a member'; filePath = $wav; mime = 'audio/wav' }
    Start-Sleep 2
    $inm = @(Arr "$base/inbound" | Where-Object { $_.body -eq 'D6 voice from a member' })[0]
    $info = if ($inm.mediaId) { Invoke-RestMethod "$base/media/$($inm.mediaId)/info" } else { $null }
    $dl = Join-Path $tmp 'downloaded.wav'; if ($inm.mediaId) { & curl.exe -s -o $dl "$base/media/$($inm.mediaId)" }
    $shaDl = if (Test-Path $dl) { (Get-FileHash $dl -Algorithm SHA256).Hash.ToLower() } else { '' }
    Check 'Media: a received file is stored (type, size, hash) and can be downloaded unchanged' 'type audio, direction in, hash equal to the original' "type $($inm.messageType); info: $($info.kind)/$($info.direction)/$($info.size); stored hash match $($info.sha256 -eq $shaWav); download hash match $($shaDl -eq $shaWav)" ($inm.messageType -eq 'audio' -and $info.direction -eq 'in' -and $info.sha256 -eq $shaWav -and $shaDl -eq $shaWav)

    # ---- allow-list: only the groups chosen for a number can be sent to and read
    $labJid = '120363000000000002@g.us'
    $null = Invoke-RestMethod -Method Put -Uri "$base/groups/allowed" -ContentType 'application/json' -Body (Body @{ groupJids = @($itJid) })
    $refused = try { Post '/messages' @{ groupName = 'Lab Test Group'; body = 'A1 to a group that is not allowed' }; 'accepted' } catch { "refused ($([int]$_.Exception.Response.StatusCode))" }
    Check 'Allow-list: a message to a group that is not allowed is refused' 'refused (400)' $refused ($refused -like 'refused (400)')
    $okA = try { $null = Post '/messages' @{ groupName = 'IT Batam'; body = 'A2 to the allowed group' }; 'accepted' } catch { 'refused' }
    Check 'Allow-list: a message to the allowed group is accepted' 'accepted' $okA ($okA -eq 'accepted')
    Sql "INSERT INTO WhatsAppLab.dbo.WhatsAppOutboundMessageDbSet (Id, GroupJid, Body, ScheduledFor, Status, Attempts, Source, SourceReference, CorrelationId, IsDeleted, CreatedBy, CreatedOn) VALUES (NEWID(), '$labJid', 'A3 sql row to a group that is not allowed', SYSUTCDATETIME(), 0, 0, 1, 'sql-demo', 'sql3', 0, 'sql', SYSUTCDATETIME())"
    $okA3 = WaitFor { (StatusOf 'A3 sql row to a group that is not allowed').status -eq 2 } 15
    $sentLab = @(Arr "$base/fake/sent" | Where-Object { $_.body -like 'A3*' }).Count
    Check 'Allow-list: a SQL-inserted row to a group that is not allowed is refused, not sent' 'status 2 (Failed), client sent nothing' "failed: $okA3, client got it: $sentLab" ($okA3 -and $sentLab -eq 0)
    $null = Post '/fake/incoming' @{ groupName = 'Lab Test Group'; sender = 'Karen'; body = 'A4 hello from a group that is not allowed' }
    $null = Post '/fake/incoming' @{ groupName = 'IT Batam'; sender = 'Karen'; body = 'A5 hello from the allowed group' }
    Start-Sleep 2
    $in4 = @(Arr "$base/inbound" | Where-Object { $_.body -like 'A4*' }).Count; $in5 = @(Arr "$base/inbound" | Where-Object { $_.body -like 'A5*' }).Count
    Check 'Allow-list: messages of a group that is not allowed are not stored' 'not allowed: 0 rows, allowed: 1 row' "not allowed: $in4, allowed: $in5" ($in4 -eq 0 -and $in5 -eq 1)
    $null = Invoke-RestMethod -Method Put -Uri "$base/groups/allowed" -ContentType 'application/json' -Body (Body @{ groupJids = @() })
    $free = try { $null = Post '/messages' @{ groupName = 'Lab Test Group'; body = 'A6 after the list is cleared' }; 'accepted' } catch { 'refused' }
    Check 'Allow-list: an empty list means no restriction' 'accepted' $free ($free -eq 'accepted')

    # ---- anti-spam: the same text to the same group is refused for 2 minutes; a template with {rand} is not a duplicate
    $null = Post '/messages' @{ groupName = 'IT Batam'; body = 'S1 exactly the same text' }
    $dup = try { Post '/messages' @{ groupName = 'IT Batam'; body = 'S1 exactly the same text' }; 'accepted' } catch { "refused ($([int]$_.Exception.Response.StatusCode))" }
    Check 'Anti-spam: the same text to the same group within 2 minutes is refused' 'refused (409)' $dup ($dup -like 'refused (409)')
    $okT = try { $null = Post '/messages' @{ groupName = 'IT Batam'; body = 'S2 varied {rand}' }; $null = Post '/messages' @{ groupName = 'IT Batam'; body = 'S2 varied {rand}' }; 'both accepted' } catch { 'refused' }
    Check 'Anti-spam: a template with {rand} gives different texts, so it is not refused' 'both accepted' $okT ($okT -eq 'both accepted')

    # ---- several numbers: each number has its own client, groups, settings, pacing, outbox and inbox
    $first = @(Arr "$base/sessions")[0].id
    $new = Post '/sessions' @{ name = 'Second number' }
    $sid = $new.id
    $list = @(Arr "$base/sessions")
    Check 'Numbers: a second number can be added' '2 numbers, the new one is Off' "$($list.Count) numbers; new one enabled = $(($list | Where-Object id -eq $sid).enabled)" ($list.Count -eq 2 -and -not ($list | Where-Object id -eq $sid).enabled)

    $g2 = Invoke-RestMethod -Method Post "$base/groups/sync?sessionId=$sid"
    $null = Post '/messages' @{ groupName = 'IT Batam'; body = 'M2 for the second number'; sessionId = $sid }
    Start-Sleep 6
    $m2 = @(Arr "$base/messages?sessionId=$sid" | Where-Object { $_.body -eq 'M2 for the second number' })[0]
    Check 'Numbers: a new number is Off, its message waits' '2 groups synced, Pending' "$($g2.synced) groups, status $($m2.status)" ($g2.synced -eq 2 -and $m2.status -eq 0)

    $null = Invoke-RestMethod -Method Put -Uri "$base/settings?sessionId=$sid" -ContentType 'application/json' -Body (Body @{ isEnabled = $true; maxMessagesPerDay = 100; minSecondsBetweenMessages = 1; receiveEnabled = $true })
    $ok = WaitFor { @(Arr "$base/messages?sessionId=$sid" | Where-Object { $_.body -eq 'M2 for the second number' })[0].status -eq 1 } 15
    $sent2 = @(Arr "$base/fake/sent?sessionId=$sid" | Where-Object { $_.body -eq 'M2 for the second number' })
    $sent1 = @(Arr "$base/fake/sent?sessionId=$first" | Where-Object { $_.body -eq 'M2 for the second number' })
    Check 'Numbers: switched on, it sends through its OWN client' 'Sent by number 2, not by number 1' "sent: $ok; client 2 got it: $($sent2.Count), client 1 got it: $($sent1.Count)" ($ok -and $sent2.Count -eq 1 -and $sent1.Count -eq 0)

    $s1 = Invoke-RestMethod "$base/status?sessionId=$first"
    $null = Invoke-RestMethod -Method Put -Uri "$base/settings?sessionId=$first" -ContentType 'application/json' -Body (Body @{ isEnabled = $true; maxMessagesPerDay = $s1.sent; minSecondsBetweenMessages = 1; receiveEnabled = $true })
    $null = Post '/messages' @{ groupName = 'IT Batam'; body = 'M4 number 1 over its cap'; sessionId = $first }
    $null = Post '/messages' @{ groupName = 'IT Batam'; body = 'M4 number 2 still free'; sessionId = $sid }
    $ok2 = WaitFor { @(Arr "$base/messages?sessionId=$sid" | Where-Object { $_.body -eq 'M4 number 2 still free' })[0].status -eq 1 } 15
    $c1 = @(Arr "$base/messages?sessionId=$first" | Where-Object { $_.body -eq 'M4 number 1 over its cap' })[0].status
    Check 'Numbers: the limits of one number do not hold back another' 'number 1 Pending, number 2 Sent' "number 1 status $c1, number 2 sent: $ok2" ($c1 -eq 0 -and $ok2)

    $null = Post '/fake/incoming' @{ groupName = 'IT Batam'; sender = 'Karen'; body = 'M5 hello number 2'; sessionId = $sid }
    Start-Sleep 2
    $in2 = @(Arr "$base/inbound?sessionId=$sid" | Where-Object { $_.body -eq 'M5 hello number 2' })
    $in1 = @(Arr "$base/inbound?sessionId=$first" | Where-Object { $_.body -eq 'M5 hello number 2' })
    Check 'Numbers: an incoming message is kept with the number that received it' 'only in the inbox of number 2' "number 2: $($in2.Count), number 1: $($in1.Count)" ($in2.Count -eq 1 -and $in1.Count -eq 0)
}
finally {
    if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force }
    $pass = ($results | Where-Object Result -eq 'PASS').Count; $fail = ($results | Where-Object Result -eq 'FAIL').Count
    Write-Host "SUMMARY: $pass PASS, $fail FAIL of $($results.Count)"
    $results | Export-Csv "$root\scripts\smoke-results.csv" -NoTypeInformation -Encoding UTF8
}

