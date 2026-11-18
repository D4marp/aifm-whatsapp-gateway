using Microsoft.Extensions.Logging;
using WhatsAppLab.Application;

namespace WhatsAppLab.Infrastructure;

/// <summary>
/// A stand-in for WhatsApp so the whole flow can be studied and tested with NO WhatsApp account and no risk.
/// It pretends to be linked, owns two groups, "sends" by logging, and can raise an incoming message on request.
/// </summary>
public class FakeWhatsAppClient : IWhatsAppClient
{
    private readonly ILogger<FakeWhatsAppClient> _logger;
    private readonly List<(string Jid, string Body, DateTime At)> _sent = new();
    private bool _failNext;

    public FakeWhatsAppClient(ILogger<FakeWhatsAppClient> logger) { _logger = logger; }

    public string LibraryName => "Fake (lab)";
    public event Func<InboundMessage, Task> MessageReceived;

    public IReadOnlyList<(string Jid, string Body, DateTime At)> Sent { get { lock (_sent) return _sent.ToList(); } }
    /// <summary>Test helper: make the next send fail once, to see the retry and back-off.</summary>
    public void FailNextSend() => _failNext = true;

    public Task<WhatsAppLinkState> GetLinkStateAsync(CancellationToken ct) => Task.FromResult(new WhatsAppLinkState(true, "+65 0000 0000", null));

    public Task<IReadOnlyList<WhatsAppGroupInfo>> ListGroupsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<WhatsAppGroupInfo>>(new List<WhatsAppGroupInfo>
        {
            new("120363000000000001@g.us", "IT Batam", 12),
            new("120363000000000002@g.us", "Lab Test Group", 3)
        });

    public Task<SendResult> SendGroupMessageAsync(string groupJid, string body, CancellationToken ct)
    {
        if (_failNext) { _failNext = false; return Task.FromResult(new SendResult(false, "Simulated failure")); }
        lock (_sent) _sent.Add((groupJid, body, DateTime.UtcNow));
        _logger.LogInformation("[FAKE WhatsApp] to {Group}: {Body}", groupJid, body);
        return Task.FromResult(new SendResult(true, null));
    }

    private readonly List<(string Jid, string FileName, string Mime, string Caption, string Sha256, long Size)> _sentMedia = new();
    public IReadOnlyList<(string Jid, string FileName, string Mime, string Caption, string Sha256, long Size)> SentMedia { get { lock (_sentMedia) return _sentMedia.ToList(); } }

    /// <summary>The fake "sends" a file by reading it and remembering its hash, so a test can prove the bytes that arrived are the bytes that were uploaded.</summary>
    public Task<SendResult> SendGroupMediaAsync(string groupJid, string filePath, string mimeType, string caption, CancellationToken ct)
    {
        if (_failNext) { _failNext = false; return Task.FromResult(new SendResult(false, "Simulated failure")); }
        if (!File.Exists(filePath)) return Task.FromResult(new SendResult(false, "File not found"));
        using var fs = File.OpenRead(filePath);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs)).ToLowerInvariant();
        lock (_sentMedia) _sentMedia.Add((groupJid, Path.GetFileName(filePath), mimeType, caption, sha, new FileInfo(filePath).Length));
        _logger.LogInformation("[FAKE WhatsApp] file to {Group}: {File} ({Mime})", groupJid, Path.GetFileName(filePath), mimeType);
        return Task.FromResult(new SendResult(true, null));
    }

    /// <summary>Test helper: pretend someone sent a file in a group. The file is copied to a temporary path, like a real client would save a download.</summary>
    public Task SimulateIncomingMediaAsync(string groupJid, string sender, string caption, string sourcePath, string mime)
    {
        if (MessageReceived == null) return Task.CompletedTask;
        var tmp = Path.Combine(Path.GetTempPath(), "wa-fake-" + Guid.NewGuid().ToString("N") + Path.GetExtension(sourcePath));
        File.Copy(sourcePath, tmp);
        return MessageReceived.Invoke(new InboundMessage(groupJid, sender + "@s.whatsapp.net", sender, caption, DateTime.UtcNow, tmp, mime));
    }

    /// <summary>Test helper: pretend someone wrote in a group.</summary>
    public Task SimulateIncomingAsync(string groupJid, string sender, string body) =>
        MessageReceived == null ? Task.CompletedTask : MessageReceived.Invoke(new InboundMessage(groupJid, sender + "@s.whatsapp.net", sender, body, DateTime.UtcNow));
}

/// <summary>
/// THE REAL CLIENT (lab version). It drives WhatsApp Web in the Microsoft Edge that is already installed, using Playwright directly
/// (the NuGet package WhatsApp.Client 1.0.7 failed: WhatsApp's Content Security Policy blocked its scripts; here the browser context bypasses it).
/// It works like a person at the screen: shows the QR, reads the chat list, searches a chat by name, types, presses Enter.
/// The login lives in an Edge profile folder OUTSIDE the project (%LOCALAPPDATA%\aifm-whatsapp-lab\session\edge-profile): it is the key to the account, never commit or zip it.
/// Limits of this lab version: the group id is "chat:" + the chat name; member counts are not read; incoming messages are not read yet.
/// </summary>
public class RealWhatsAppClient : IWhatsAppClient, IWhatsAppSessionControl, IAsyncDisposable
{
    // One browser profile (= one login = one number) per client. The folder is <root>\<profileKey>; the root can be moved with WHATSAPP_PROFILE_ROOT (e.g. for a test instance that must not touch the real logins).
    private readonly string _profileKey;
    private string ProfileDir => Path.Combine(Environment.GetEnvironmentVariable("WHATSAPP_PROFILE_ROOT") is { Length: > 0 } root ? root : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "aifm-whatsapp-lab", "session"), _profileKey);
    private string _connection = "disconnected";
    public string Connection => _connection;

    public async Task ConnectAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { _connection = "loading"; await EnsurePageAsync(); }
        catch { _connection = "disconnected"; throw; }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        _loopCts?.Cancel();   // stop the receive loop first so it does not keep the gate while we wait
        await _gate.WaitAsync(ct);
        try { await CloseBrowserAsync(); }
        finally { _gate.Release(); }
    }

    public async Task ResetSessionAsync(CancellationToken ct)
    {
        _loopCts?.Cancel();
        await _gate.WaitAsync(ct);
        try
        {
            await CloseBrowserAsync();
            if (Directory.Exists(ProfileDir)) Directory.Delete(ProfileDir, true);   // forgets the login: the next Connect shows a new QR
        }
        finally { _gate.Release(); }
    }

    private async Task CloseBrowserAsync()
    {
        _loopCts?.Cancel();
        if (_context != null) { try { await _context.CloseAsync(); } catch { } }
        _context = null; _page = null; _connection = "disconnected"; _visited.Clear();
    }

    private readonly ILogger<RealWhatsAppClient> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Microsoft.Playwright.IPlaywright _pw;
    private Microsoft.Playwright.IBrowserContext _context;
    private Microsoft.Playwright.IPage _page;

    // WhatsApp:ShowBrowser = true shows the Edge window (for debugging). Default: hidden, everything runs behind the scenes.
    private readonly bool _showBrowser;
    public RealWhatsAppClient(ILogger<RealWhatsAppClient> logger, Microsoft.Extensions.Configuration.IConfiguration config, string profileKey = "edge-profile")
    {
        _logger = logger;
        _profileKey = string.IsNullOrWhiteSpace(profileKey) ? "edge-profile" : profileKey;
        _showBrowser = string.Equals(config["WhatsApp:ShowBrowser"], "true", StringComparison.OrdinalIgnoreCase);
    }

    public string LibraryName => "Real: WhatsApp Web via Playwright + Edge (lab)";
    public event Func<InboundMessage, Task> MessageReceived;

    // ---- receiving: every few seconds, look for GROUPS that show an unread badge, open them, read the new incoming messages.
    // Side effect to know: opening a chat marks it as read on the phone. Only groups already known (synced) are looked at.
    private volatile bool _receiving;
    private HashSet<string> _groupTitles = new();
    private readonly HashSet<string> _seen = new();      // message ids already handed over
    private readonly HashSet<string> _visited = new();   // chats read at least once since connecting
    private readonly Dictionary<string, string> _lastSig = new();   // last seen row text per group (preview + time)
    private CancellationTokenSource _loopCts;

    public void SetReceiving(bool on) => _receiving = on;

    // Fix for the "own messages stored as incoming" bug (16 Nov: 14 rows). The direction check on the page misses some outgoing messages,
    // so two extra guards: (1) the sender name this number uses, learned right after each send; (2) texts this number sent in the last 30 minutes.
    // Written 18 Nov. It only compiles so far: the Fake client does not run this code, so it must be confirmed on the live page.
    private readonly HashSet<string> _ownNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Text, DateTime At)> _recentSent = new();
    private void RememberSent(string text)
    {
        _recentSent.Enqueue(((text ?? "").Trim(), DateTime.UtcNow));
        while (_recentSent.TryPeek(out var old) && (DateTime.UtcNow - old.At).TotalMinutes > 30) _recentSent.TryDequeue(out _);
    }
    private bool IsOwnMessage(string sender, string text)
    {
        if (!string.IsNullOrEmpty(sender) && _ownNames.Contains(sender)) return true;
        var t = (text ?? "").Trim();
        return t.Length > 0 && _recentSent.Any(r => r.Text == t && (DateTime.UtcNow - r.At).TotalMinutes <= 30);
    }
    private async Task LearnOwnNameAsync(Microsoft.Playwright.IPage p)
    {
        try
        {
            var msgs = await p.EvaluateAsync<MsgInfo[]>(MessagesJs);
            var last = msgs.LastOrDefault(m => m.pre != null && m.dir == "out");
            if (last != null && System.Text.RegularExpressions.Regex.Match(last.pre, @"\]\s*(.*?):\s*$") is { Success: true } sm && _ownNames.Add(sm.Groups[1].Value))
                _logger.LogInformation("[REAL WhatsApp] learned the sender name of this number");
        }
        catch { }   // only a helper: sending already succeeded
    }
    private volatile HashSet<string> _allowedTitles = new();
    public void SetAllowedGroups(IReadOnlyCollection<string> names) => _allowedTitles = new HashSet<string>(names ?? Array.Empty<string>());

    // Direction (checked on the live page 16 Nov): ids and message-in/out classes carry no direction. An outgoing message has a tail-out icon and/or the labels "You:" / "Delivered" / "Read" / "Sent"; everything else is incoming.
    private const string MessagesJs =@"() => [...document.querySelectorAll('#main div[data-id]')].map(m => { const pre = m.querySelector('[data-pre-plain-text]'); const txt = m.querySelector('span.selectable-text'); const isOut = !!m.querySelector(""[data-icon='tail-out']"") || [...m.querySelectorAll('[aria-label]')].some(a => /^(You:|Delivered|Read|Sent|Pending)\s*$/.test(a.getAttribute('aria-label'))); const dir = isOut ? 'out' : 'in'; return { id: m.getAttribute('data-id'), pre: pre ? pre.getAttribute('data-pre-plain-text') : null, text: txt ? txt.innerText : null, dir: dir }; })";
    private sealed class MsgInfo { public string id { get; set; } public string pre { get; set; } public string text { get; set; } public string dir { get; set; } }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), token); } catch { return; }
            if (!_receiving || _groupTitles.Count == 0 || MessageReceived == null) continue;
            if (!await _gate.WaitAsync(0)) continue;   // sending or another action is running: try again in 10 seconds
            try
            {
                var p = _page;
                if (p == null || p.IsClosed || !await ChatListVisibleAsync(p)) continue;
                await p.Keyboard.PressAsync("Escape");
                await ClearSearchAsync(p);
                var rows = await ReadRowsAsync(p);
                foreach (var r in rows.Where(r => r.unread != null))
                    _logger.LogInformation("[REAL WhatsApp] receive: unread badge on '{Title}' ({Badge}), known group: {Known}", r.title, r.unread, _groupTitles.Contains(r.title));
                // Found 16 Nov: the badge is gone when the phone already shows/reads the chat. So also treat a changed row (preview text or time) as new activity.
                var allowedNow = _allowedTitles;   // empty = no restriction; otherwise only these groups are ever opened
                var known = rows.Where(r => r.title != null && _groupTitles.Contains(r.title) && (allowedNow.Count == 0 || allowedNow.Contains(r.title))).ToList();
                var changed = known.Where(r => r.unread != null || (_lastSig.TryGetValue(r.title, out var old) && old != r.text)).Take(3).ToList();
                foreach (var r in known) if (!_lastSig.ContainsKey(r.title)) _lastSig[r.title] = r.text;   // first look: remember, do not replay old messages
                foreach (var r in changed) _lastSig[r.title] = r.text;
                foreach (var row in changed)
                {
                    var n = System.Text.RegularExpressions.Regex.Match(row.unread ?? "1", @"\d+") is { Success: true } mm ? int.Parse(mm.Value) : 1;
                    await OpenChatAsync(p, row.title);
                    MsgInfo[] msgs = Array.Empty<MsgInfo>();
                    for (var k = 0; k < 10 && msgs.Length == 0; k++)   // headless loads the conversation slower: wait up to ~5 s for message nodes
                    {
                        await Task.Delay(500);
                        msgs = await p.EvaluateAsync<MsgInfo[]>(MessagesJs);
                    }
                    // Found 16 Nov: data-id no longer starts with false_/true_ (it is a bare id), so direction comes from the message-in / message-out class.
                    var incoming = msgs.Where(m => m.id != null && m.dir == "in" && m.text != null).ToList();
                    _logger.LogInformation("[REAL WhatsApp] receive: opened '{Title}': {Nodes} message nodes, {Incoming} incoming, {WithText} with text; last: {Last}",
                        row.title, msgs.Length, incoming.Count, msgs.Count(m => m.text != null),
                        string.Join(",", msgs.TakeLast(3).Select(m => (m.dir ?? "?") + "/" + (m.pre != null ? "pre" : "nopre") + "/" + (m.text != null ? "txt" : "notxt"))));
                    List<MsgInfo> fresh;
                    if (_visited.Add(row.title)) fresh = incoming.TakeLast(Math.Min(n, 10)).ToList();   // first time: only what is unread (at most 10)
                    else fresh = incoming.Where(m => !_seen.Contains(m.id)).ToList();
                    foreach (var m in incoming) _seen.Add(m.id);
                    foreach (var m in fresh)
                    {
                        var who = m.pre != null && System.Text.RegularExpressions.Regex.Match(m.pre, @"\]\s*(.*?):\s*$") is { Success: true } sm ? sm.Groups[1].Value : "unknown";
                        if (IsOwnMessage(who, m.text)) { _logger.LogInformation("[REAL WhatsApp] receive: skipped a message sent by this number in {Group}", row.title); continue; }
                        _logger.LogInformation("[REAL WhatsApp] received in {Group} from {Sender}", row.title, who);
                        await MessageReceived.Invoke(new InboundMessage("chat:" + row.title, who + "@lab", who, m.text, DateTime.UtcNow));
                    }
                    await p.Keyboard.PressAsync("Escape");
                    await ClearSearchAsync(p);
                }
            }
            catch (Exception ex) { _logger.LogWarning("Receive cycle failed: {Msg}", ex.Message.Split('\n')[0]); }
            finally { _gate.Release(); }
        }
    }

    private async Task<Microsoft.Playwright.IPage> EnsurePageAsync()
    {
        if (_page != null && !_page.IsClosed) return _page;
        var profile = ProfileDir;
        Directory.CreateDirectory(profile);
        _pw ??= await Microsoft.Playwright.Playwright.CreateAsync();
        _context = await _pw.Chromium.LaunchPersistentContextAsync(profile, new Microsoft.Playwright.BrowserTypeLaunchPersistentContextOptions
        {
            Channel = "msedge", Headless = !_showBrowser, BypassCSP = true, Args = new[] { "--disable-quic" },   // QUIC failed on this network (ERR_QUIC_PROTOCOL_ERROR)
 ViewportSize = new Microsoft.Playwright.ViewportSize { Width = 1100, Height = 800 }
        });
        _page = _context.Pages.FirstOrDefault() ?? await _context.NewPageAsync();
        await _page.GotoAsync("https://web.whatsapp.com/");
        _loopCts = new CancellationTokenSource();
        _ = Task.Run(() => ReceiveLoopAsync(_loopCts.Token));
        return _page;
    }

    private static async Task<bool> ChatListVisibleAsync(Microsoft.Playwright.IPage p) => await p.Locator("#pane-side").CountAsync() > 0;

    public async Task<WhatsAppLinkState> GetLinkStateAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_page == null || _page.IsClosed) { _connection = "disconnected"; return new WhatsAppLinkState(false, null, null); }   // not connected: never starts the browser by itself
            var p = _page;
            // "WhatsApp is open in another window. Click Use here": this window takes the session back (it is ours: a second window of the lab, or one that was closed a moment ago)
            var useHere = p.GetByRole(Microsoft.Playwright.AriaRole.Button, new Microsoft.Playwright.PageGetByRoleOptions { Name = "Use here" });
            if (await useHere.CountAsync() > 0)
            {
                _logger.LogInformation("[REAL WhatsApp] 'open in another window' dialog: clicking Use here");
                await useHere.First.ClickAsync(new Microsoft.Playwright.LocatorClickOptions { Force = true, Timeout = 4000 });
                await Task.Delay(2500);
            }
            if (await ChatListVisibleAsync(p)) { _connection = "linked"; return new WhatsAppLinkState(true, "(linked)", null); }
            var qr = p.Locator("div[data-ref]").First;
            if (await qr.CountAsync() > 0)
            {
                var png = await qr.ScreenshotAsync(new Microsoft.Playwright.LocatorScreenshotOptions { Timeout = 3000 });
                _connection = "qr";
                return new WhatsAppLinkState(false, null, "data:image/png;base64," + Convert.ToBase64String(png));
            }
            _connection = "loading";
            return new WhatsAppLinkState(false, null, null);   // still loading
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Link state failed"); return new WhatsAppLinkState(false, null, null); }
        finally { _gate.Release(); }
    }

    // ---- groups: only real groups (the "Groups" filter of WhatsApp Web), with the member count read from the group info panel
    private const string ChatRows = "#pane-side [role='row'], #pane-side [role='listitem']";
    private const string RowsJs = @"els => els.map(r => { const s = r.querySelector('span[title]'); const u = r.querySelector(""span[aria-label*='unread']""); return { title: s ? s.getAttribute('title') : null, unread: u ? u.getAttribute('aria-label') : null, text: r.innerText }; })";
    private sealed class RowInfo { public string title { get; set; } public string unread { get; set; } public string text { get; set; } }

    private async Task<List<RowInfo>> ReadRowsAsync(Microsoft.Playwright.IPage p)
    {
        var rows = await p.Locator(ChatRows).EvaluateAllAsync<RowInfo[]>(RowsJs);
        return rows.Where(r => !string.IsNullOrWhiteSpace(r.title)).GroupBy(r => r.title).Select(g => g.First()).ToList();
    }

    private async Task ClickChipAsync(Microsoft.Playwright.IPage p, string name)
    {
        // the filter chips ("All", "Unread", "Favorites", "Groups") are matched by their text, whatever element or role WhatsApp uses for them
        var chip = p.Locator("#side").GetByText(new System.Text.RegularExpressions.Regex("^" + name + @"\s*\d*$")).First;
        var found = await chip.CountAsync() > 0;
        _logger.LogInformation("[REAL WhatsApp] filter chip '{Name}' found: {Found}", name, found);
        if (found) { await chip.ClickAsync(new Microsoft.Playwright.LocatorClickOptions { Force = true, Timeout = 4000 }); await Task.Delay(900); }
    }

    // Found 16 Nov: Escape does not empty the search box, and while it holds text the chat list shows search results, which carry no unread badge, so receiving saw nothing.
    private static async Task ClearSearchAsync(Microsoft.Playwright.IPage p)
    {
        try
        {
            var search = p.Locator("#side input[role='textbox']").First;
            if ((await search.InputValueAsync()).Length == 0) return;
            await search.FocusAsync();
            await p.Keyboard.PressAsync("Control+A");
            await p.Keyboard.PressAsync("Backspace");
            await p.Keyboard.PressAsync("Escape");
            await Task.Delay(400);
        }
        catch { }
    }

    private async Task OpenChatAsync(Microsoft.Playwright.IPage p, string name)
    {
        var search = p.Locator("#side input[role='textbox']").First;   // seen on the live page: an INPUT with aria-label "Search or start a new chat"
        await p.Keyboard.PressAsync("Escape");   // WhatsApp shows tooltips and banners that sit on top of the search box and swallow the click
        await search.FocusAsync();               // focus does not depend on the box being the top element
        await p.Keyboard.PressAsync("Control+A");
        await p.Keyboard.PressAsync("Backspace");
        await p.Keyboard.TypeAsync(name, new Microsoft.Playwright.KeyboardTypeOptions { Delay = 40 });
        var hit = p.Locator("#pane-side span[title]").Filter(new Microsoft.Playwright.LocatorFilterOptions { HasTextString = name }).First;
        await hit.WaitForAsync(new Microsoft.Playwright.LocatorWaitForOptions { Timeout = 8000 });
        await hit.ClickAsync(new Microsoft.Playwright.LocatorClickOptions { Force = true, Timeout = 8000 });
    }

    private async Task<int> ReadMemberCountAsync(Microsoft.Playwright.IPage p, string name)
    {
        try
        {
            await OpenChatAsync(p, name);
            await p.Locator("#main header").First.ClickAsync(new Microsoft.Playwright.LocatorClickOptions { Force = true, Timeout = 5000 });   // opens the group info panel
            // the panel fills in a moment after it opens (slower when the browser is hidden): wait up to ~8 seconds for the "N members" text
            var text = ""; System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Match.Empty;
            for (var i = 0; i < 12 && !m.Success; i++)
            {
                await Task.Delay(700);
                text = await p.Locator("#app").InnerTextAsync(new Microsoft.Playwright.LocatorInnerTextOptions { Timeout = 5000 });
                m = System.Text.RegularExpressions.Regex.Match(text, @"Group\s*[·•\-]\s*(\d[\d.,]*)\s+(members?|participants?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!m.Success) m = System.Text.RegularExpressions.Regex.Match(text, @"(\d[\d.,]*)\s+(members?|participants?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            if (!m.Success) _logger.LogWarning("Member count: no 'N members' text for '{Name}'. Text seen around 'Group': {Snippet}", name, System.Text.RegularExpressions.Regex.Match(text, @".{0,60}Group.{0,80}", System.Text.RegularExpressions.RegexOptions.Singleline).Value.Replace("\n", " | "));
            await p.Keyboard.PressAsync("Escape"); await p.Keyboard.PressAsync("Escape");
            var n = m.Success ? int.Parse(System.Text.RegularExpressions.Regex.Replace(m.Groups[1].Value, @"[.,]", "")) : 0;
            _logger.LogInformation("[REAL WhatsApp] group '{Name}': {Count} members", name, n);
            return n;
        }
        catch (Exception ex) { _logger.LogWarning("Member count failed for '{Name}': {Msg}", name, ex.Message.Split('\n')[0]); return 0; }
    }

    public async Task<IReadOnlyList<WhatsAppGroupInfo>> ListGroupsAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var p = _page;
            if (p == null || p.IsClosed || !await ChatListVisibleAsync(p)) return new List<WhatsAppGroupInfo>();
            await p.Keyboard.PressAsync("Escape");
            await ClickChipAsync(p, "Groups");
            var groups = await ReadRowsAsync(p);
            await ClickChipAsync(p, "All");
            _groupTitles = groups.Select(g => g.title).ToHashSet();

            var result = new List<WhatsAppGroupInfo>();
            foreach (var g in groups.Take(30))
            {
                var count = await ReadMemberCountAsync(p, g.title);
                result.Add(new WhatsAppGroupInfo("chat:" + g.title, g.title, count));
            }
            await p.Keyboard.PressAsync("Escape");
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<SendResult> SendGroupMessageAsync(string groupJid, string body, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var p = _page;
            if (p == null || p.IsClosed || !await ChatListVisibleAsync(p)) return new SendResult(false, "Not linked: connect and scan the QR first");
            var name = groupJid.StartsWith("chat:") ? groupJid.Substring(5) : groupJid;

            await OpenChatAsync(p, name);

            var box = p.Locator("footer div[contenteditable='true']").First;
            await box.WaitForAsync(new Microsoft.Playwright.LocatorWaitForOptions { Timeout = 8000 });
            await box.FocusAsync();
            var lines = body.Replace("\r", "").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) await p.Keyboard.PressAsync("Shift+Enter");
                await p.Keyboard.TypeAsync(lines[i], new Microsoft.Playwright.KeyboardTypeOptions { Delay = 15 });
            }
            await p.Keyboard.PressAsync("Enter");
            await Task.Delay(1500, ct);
            RememberSent(body);
            await LearnOwnNameAsync(p);
            await ClearSearchAsync(p);   // empty the search so the chat list is back to normal (and shows unread badges)
            _logger.LogInformation("[REAL WhatsApp] sent to {Name}", name);
            return new SendResult(true, null);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Send failed"); return new SendResult(false, ex.Message); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Sends a file through the attach menu of WhatsApp Web. Selectors checked on the live page on 18 Nov with the read-only probes:
    /// attach button "Attach"; menu items "Audio" / "Photos &amp; videos" / "Document"; preview send button "Send 1 selected";
    /// caption box "Type a message" inside the preview (images and videos only; the audio preview has NO caption box).
    /// Audio with text: the audio is sent first, then the text as a second message right after it (WhatsApp Web cannot put a caption on audio).
    /// The send counts as done only when the preview has closed; otherwise it fails, so the outbox never says Sent for a file that stayed in the preview.
    /// An audio file goes as an audio attachment, not as a voice note.
    /// </summary>
    public async Task<SendResult> SendGroupMediaAsync(string groupJid, string filePath, string mimeType, string caption, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var p = _page;
            if (p == null || p.IsClosed || !await ChatListVisibleAsync(p)) return new SendResult(false, "Not linked: connect and scan the QR first");
            if (!File.Exists(filePath)) return new SendResult(false, "File not found: " + Path.GetFileName(filePath));
            var name = groupJid.StartsWith("chat:") ? groupJid.Substring(5) : groupJid;

            await OpenChatAsync(p, name);
            await p.Locator("footer div[contenteditable='true']").First.WaitForAsync(new Microsoft.Playwright.LocatorWaitForOptions { Timeout = 8000 });

            // 0) remove a file left in the composer by an earlier attempt (seen 18 Nov: "Remove attachment" in the preview). Only that button is pressed;
            //    Escape is NOT used here because it also closed the open chat. Nothing is sent by this.
            var leftover = p.Locator("[aria-label='Remove attachment']");
            if (await leftover.CountAsync() > 0)
            {
                await leftover.First.ClickAsync(new Microsoft.Playwright.LocatorClickOptions { Force = true, Timeout = 4000 });
                await Task.Delay(700, ct);
            }

            // 1) open the attach menu (button aria-label "Attach") and wait until the wanted item is really visible.
            //    18 Nov: once the item was not there yet ("waiting for button[aria-label='Audio']"), so the menu is opened again if needed.
            var item = MenuItemFor(mimeType);
            var isAudio = item == "Audio";
            await OpenAttachMenuAsync(p, item);

            // 2) pick the menu item by type; WhatsApp creates the file input only after the item is clicked, so wait for the file chooser instead
            //    (menu items seen 18 Nov: "Audio", "Photos & videos", "Document")
            var chooser = await p.RunAndWaitForFileChooserAsync(
                () => p.Locator($"button[aria-label='{item}']").First.ClickAsync(new Microsoft.Playwright.LocatorClickOptions { Force = true, Timeout = 6000 }),
                new Microsoft.Playwright.PageRunAndWaitForFileChooserOptions { Timeout = 8000 });
            await chooser.SetFilesAsync(filePath);

            // 3) wait for the preview's send button; a large video needs time to be processed before it appears
            var send = p.Locator("[aria-label='Send 1 selected']").First;
            await send.WaitForAsync(new Microsoft.Playwright.LocatorWaitForOptions { Timeout = 90000 });
            await Task.Delay(800, ct);

            // 4) caption: images and videos have a caption box in the preview; audio does not (its text is sent after the file, step 6)
            var hasText = !string.IsNullOrWhiteSpace(caption);
            if (hasText && !isAudio)
            {
                var cap = p.Locator("div[contenteditable='true'][aria-label='Type a message']").First;
                await cap.WaitForAsync(new Microsoft.Playwright.LocatorWaitForOptions { Timeout = 8000 });
                await cap.FocusAsync();
                await TypeLinesAsync(p, caption);
            }

            // 5) send, then wait until the preview is gone (upload done). Still open = not sent: fail instead of reporting Sent.
            await send.ClickAsync(new Microsoft.Playwright.LocatorClickOptions { Force = true, Timeout = 8000 });
            var closed = false;
            for (var i = 0; i < 120 && !closed; i++)   // up to about 2 minutes for a large video
            {
                await Task.Delay(1000, ct);
                closed = await p.Locator("[aria-label='Remove attachment']").CountAsync() == 0 && await p.Locator("[aria-label='Send 1 selected']").CountAsync() == 0;
            }
            if (!closed) return new SendResult(false, "The file preview did not close after pressing send; the file was probably not sent. It is still in the composer and is removed on the next attempt.");
            if (hasText && !isAudio) RememberSent(caption);

            // 6) audio with text: the text goes as the next message, straight after the audio
            if (hasText && isAudio)
            {
                var box = p.Locator("footer div[contenteditable='true']").First;
                await box.WaitForAsync(new Microsoft.Playwright.LocatorWaitForOptions { Timeout = 8000 });
                await box.FocusAsync();
                await TypeLinesAsync(p, caption);
                await p.Keyboard.PressAsync("Enter");
                await Task.Delay(1500, ct);
                RememberSent(caption);
            }

            await ClearSearchAsync(p);
            _logger.LogInformation("[REAL WhatsApp] file sent to {Name}: {File}{Text}", name, Path.GetFileName(filePath), hasText ? (isAudio ? " + text as next message" : " with caption") : "");
            return new SendResult(true, null);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Send file failed"); return new SendResult(false, ex.Message); }   // full message: the call log says which selector was not found (the dispatcher cuts it to 1000 characters)
        finally { _gate.Release(); }
    }

    /// <summary>
    /// READ-ONLY probe (18 Nov): opens a chat, opens the attach menu, lists the file inputs and the menu items, then closes the menu. Sends nothing.
    /// Used to find the real selectors for SendGroupMediaAsync.
    /// </summary>
    public async Task<object> AttachProbeAsync(string groupName)
    {
        await _gate.WaitAsync();
        try
        {
            var p = _page;
            if (p == null || p.IsClosed || !await ChatListVisibleAsync(p)) return new { error = "Not linked: connect first." };
            await OpenChatAsync(p, groupName);
            await p.Locator("footer div[contenteditable='true']").First.WaitForAsync(new Microsoft.Playwright.LocatorWaitForOptions { Timeout = 8000 });

            var footerBefore = await p.EvaluateAsync<string[]>(@"() => [...document.querySelectorAll('footer [data-icon], footer button, footer [role=button]')].slice(0, 40).map(e => e.tagName + ' icon=' + (e.getAttribute('data-icon') || '-') + ' aria=' + (e.getAttribute('aria-label') || '-') + ' title=' + (e.getAttribute('title') || '-'))");

            var clicked = "none";
            foreach (var sel in new[] { "footer [data-icon='plus']", "footer [data-icon='attach-menu-plus']", "footer [data-icon='clip']", "footer button[title='Attach']", "footer [aria-label='Attach']" })
            {
                var loc = p.Locator(sel).First;
                if (await loc.CountAsync() == 0) continue;
                try { await loc.ClickAsync(new Microsoft.Playwright.LocatorClickOptions { Force = true, Timeout = 3000 }); clicked = sel; break; }
                catch { }
            }
            await Task.Delay(1200);

            var inputs = await p.EvaluateAsync<string[]>(@"() => [...document.querySelectorAll('input[type=file]')].map(i => 'accept=' + (i.getAttribute('accept') || '(none)') + ' multiple=' + i.multiple + ' inMain=' + !!i.closest('#main') + ' visible=' + (i.offsetParent !== null))");
            var menuItems = await p.EvaluateAsync<string[]>(@"() => [...document.querySelectorAll('[data-icon], [aria-label], [title]')].filter(e => e.offsetParent !== null && /document|image|camera|contact|poll|sticker|photo|video|audio|attach|file/i.test((e.getAttribute('data-icon') || '') + ' ' + (e.getAttribute('aria-label') || '') + ' ' + (e.getAttribute('title') || ''))).slice(0, 40).map(e => e.tagName + ' icon=' + (e.getAttribute('data-icon') || '-') + ' aria=' + (e.getAttribute('aria-label') || '-') + ' title=' + (e.getAttribute('title') || '-') + ' text=' + (e.innerText || '').slice(0, 30).replace(/\s+/g, ' '))");

            await p.Keyboard.PressAsync("Escape");
            await Task.Delay(300);
            await p.Keyboard.PressAsync("Escape");
            await ClearSearchAsync(p);
            return new { clicked, footerBefore, inputs, menuItems };
        }
        catch (Exception ex) { return new { error = ex.Message.Split('\n')[0] }; }
        finally { _gate.Release(); }
    }

    // what a message node holds, read from the page (used by the chat probe and by the delivery check after sending a file)
    private const string LastMessagesJs = @"(n) => [...document.querySelectorAll('#main div[data-id]')].slice(-n).map(m => { const t = m.querySelector('span.selectable-text'); const out = !!m.querySelector(""[data-icon='tail-out']"") || [...m.querySelectorAll('[aria-label]')].some(a => /^(You:|Delivered|Read|Sent|Pending)\s*$/.test(a.getAttribute('aria-label'))) || !!m.querySelector(""[data-icon='msg-check'],[data-icon='msg-dblcheck'],[data-icon='msg-time']""); return { id: m.getAttribute('data-id'), dir: out ? 'out' : 'in', audio: !!m.querySelector(""audio,[data-icon*='audio'],[aria-label*='voice' i],[aria-label^='Play' i]""), image: !!m.querySelector(""img[src^='blob:']""), video: !!m.querySelector(""video,[data-icon*='video'],[data-icon='media-play']""), text: t ? t.innerText.slice(0, 80) : null, icons: [...m.querySelectorAll('[data-icon]')].map(i => i.getAttribute('data-icon')).slice(0, 8).join(',') }; })";
    private sealed class NodeInfo { public string id { get; set; } public string dir { get; set; } public bool audio { get; set; } public bool image { get; set; } public bool video { get; set; } public string text { get; set; } public string icons { get; set; } }

    /// <summary>READ-ONLY: the last messages of a chat (direction, audio/image/video, text) and whether a file preview is still open. Sends nothing.</summary>
    public async Task<object> ChatProbeAsync(string groupName, int last = 10)
    {
        await _gate.WaitAsync();
        try
        {
            var p = _page;
            if (p == null || p.IsClosed || !await ChatListVisibleAsync(p)) return new { error = "Not linked: connect first." };
            var previewOpen = await p.Locator("[aria-label='Remove attachment']").CountAsync() > 0;
            if (!previewOpen) await OpenChatAsync(p, groupName);
            await Task.Delay(1500);
            var nodes = await p.EvaluateAsync<NodeInfo[]>(LastMessagesJs, last);
            if (!previewOpen) await ClearSearchAsync(p);
            return new { previewOpen, nodes };
        }
        catch (Exception ex) { return new { error = ex.Message.Split('\n')[0] }; }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Stages a file in the preview, lists the caption box and send buttons it finds, then removes the file with "Remove attachment". Sends nothing.
    /// Used to find the real selectors of the preview screen.
    /// </summary>
    public async Task<object> PreviewProbeAsync(string groupName, string filePath, string mimeType)
    {
        await _gate.WaitAsync();
        try
        {
            var p = _page;
            if (p == null || p.IsClosed || !await ChatListVisibleAsync(p)) return new { error = "Not linked: connect first." };
            var leftover = p.Locator("[aria-label='Remove attachment']");
            if (await leftover.CountAsync() > 0) { await leftover.First.ClickAsync(new Microsoft.Playwright.LocatorClickOptions { Force = true, Timeout = 4000 }); await Task.Delay(700); }
            await OpenChatAsync(p, groupName);
            await p.Locator("footer div[contenteditable='true']").First.WaitForAsync(new Microsoft.Playwright.LocatorWaitForOptions { Timeout = 8000 });
            await p.Locator("footer button[aria-label='Attach']").First.ClickAsync(new Microsoft.Playwright.LocatorClickOptions { Force = true, Timeout = 6000 });
            await Task.Delay(700);
            var item = MenuItemFor(mimeType);
            var chooser = await p.RunAndWaitForFileChooserAsync(
                () => p.Locator($"button[aria-label='{item}']").First.ClickAsync(new Microsoft.Playwright.LocatorClickOptions { Force = true, Timeout = 6000 }),
                new Microsoft.Playwright.PageRunAndWaitForFileChooserOptions { Timeout = 8000 });
            await chooser.SetFilesAsync(filePath);
            await Task.Delay(2500);

            var editables = await p.EvaluateAsync<string[]>(@"() => [...document.querySelectorAll('[contenteditable=true]')].map(e => 'aria=' + (e.getAttribute('aria-label') || '-') + ' tab=' + (e.getAttribute('data-tab') || '-') + ' inFooter=' + !!e.closest('footer') + ' inMain=' + !!e.closest('#main') + ' visible=' + (e.offsetParent !== null) + ' text=' + (e.innerText || '').slice(0, 20))");
            var sends = await p.EvaluateAsync<string[]>(@"() => [...document.querySelectorAll(""[data-icon*='send'], [aria-label='Send'], [aria-label*='Send' i]"")].map(e => { const b = e.getBoundingClientRect(); const btn = e.closest('button,[role=button]'); return e.tagName + ' icon=' + (e.getAttribute('data-icon') || '-') + ' aria=' + (e.getAttribute('aria-label') || '-') + ' btnAria=' + (btn ? (btn.getAttribute('aria-label') || '-') : 'none') + ' inFooter=' + !!e.closest('footer') + ' visible=' + (e.offsetParent !== null) + ' x=' + Math.round(b.x) + ' y=' + Math.round(b.y); })");
            var removed = false;
            if (await leftover.CountAsync() > 0) { await leftover.First.ClickAsync(new Microsoft.Playwright.LocatorClickOptions { Force = true, Timeout = 4000 }); removed = true; await Task.Delay(700); }
            var stillOpen = await leftover.CountAsync() > 0;
            await ClearSearchAsync(p);
            return new { item, editables, sends, removed, stillOpen };
        }
        catch (Exception ex) { return new { error = ex.Message.Split('\n')[0] }; }
        finally { _gate.Release(); }
    }

    // clicks "Attach" and waits for the menu item; clicks "Attach" once more when the menu did not open
    private static async Task OpenAttachMenuAsync(Microsoft.Playwright.IPage p, string item)
    {
        var attach = p.Locator("footer button[aria-label='Attach']").First;
        var wanted = p.Locator($"button[aria-label='{item}']").First;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await attach.ClickAsync(new Microsoft.Playwright.LocatorClickOptions { Force = true, Timeout = 6000 });
            try
            {
                await wanted.WaitForAsync(new Microsoft.Playwright.LocatorWaitForOptions { State = Microsoft.Playwright.WaitForSelectorState.Visible, Timeout = 4000 });
                return;
            }
            catch (TimeoutException) { await Task.Delay(500); }   // the menu did not open (or closed again): try once more
        }
        throw new TimeoutException($"The attach menu did not show '{item}' after two tries.");
    }

    // types text into the focused box; Shift+Enter keeps line breaks inside one message
    private static async Task TypeLinesAsync(Microsoft.Playwright.IPage p, string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) await p.Keyboard.PressAsync("Shift+Enter");
            await p.Keyboard.TypeAsync(lines[i], new Microsoft.Playwright.KeyboardTypeOptions { Delay = 15 });
        }
    }

    // attach-menu item per file type (seen on the live page 18 Nov: "Audio", "Photos & videos", "Document")
    private static string MenuItemFor(string mimeType) =>
        mimeType != null && mimeType.StartsWith("audio/") ? "Audio"
        : mimeType != null && (mimeType.StartsWith("video/") || mimeType.StartsWith("image/")) ? "Photos & videos"
        : "Document";

    /// <summary>Lab diagnostics: the editable fields and the main containers of the page, and a screenshot saved next to the session folder.</summary>
    public async Task<object> DebugAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var p = _page;
            if (p == null || p.IsClosed) return new { shot = (string)null, info = (object)"not connected" };
            var shot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "aifm-whatsapp-lab", "debug.png");
            await p.ScreenshotAsync(new Microsoft.Playwright.PageScreenshotOptions { Path = shot });
            var info = await p.EvaluateAsync<object>(@"() => ({
                editable: [...document.querySelectorAll('[contenteditable=true], input, [role=textbox], [role=searchbox]')].map(e => ({ tag: e.tagName, role: e.getAttribute('role'), tab: e.getAttribute('data-tab'), aria: e.getAttribute('aria-label'), ph: e.getAttribute('placeholder') || e.getAttribute('data-placeholder'), inFooter: !!e.closest('footer'), inSide: !!e.closest('#side'), inPane: !!e.closest('#pane-side') })),
                ids: ['side','pane-side','main','app'].map(i => i + ':' + !!document.getElementById(i)),
                buttons: [...document.querySelectorAll('button[aria-label], [role=button][aria-label]')].slice(0, 20).map(e => e.getAttribute('aria-label'))
            })");
            // fixed read-only structure dump of the last messages of the first known group (no message text): used to find how outgoing and incoming messages differ
            object msgs = null; List<string> dirs = null;
            var first = _groupTitles.FirstOrDefault();
            if (first != null && await ChatListVisibleAsync(p))
            {
                await OpenChatAsync(p, first);
                await Task.Delay(2500);
                msgs = await p.EvaluateAsync<object>(@"() => [...document.querySelectorAll('#main div[data-id]')].slice(-8).map(m => { const pre = m.querySelector('[data-pre-plain-text]'); return { cls: m.className, parentCls: m.parentElement ? m.parentElement.className : null, role: m.getAttribute('role'), icons: [...m.querySelectorAll('[data-icon]')].map(i => i.getAttribute('data-icon')), arias: [...m.querySelectorAll('[aria-label]')].map(i => i.getAttribute('aria-label')).slice(0, 4), sender: pre ? pre.getAttribute('data-pre-plain-text').replace(/^\[[^\]]*\]\s*/, '') : null, hasText: !!m.querySelector('span.selectable-text') }; })");
                var all = await p.EvaluateAsync<MsgInfo[]>(MessagesJs);
                dirs = all.TakeLast(8).Select(m => (m.dir ?? "?") + " / " + (m.pre != null ? m.pre.Substring(m.pre.IndexOf(']') + 1).Trim() : "-")).ToList();   // what the receive loop would decide for each
                await ClearSearchAsync(p);
            }
            return new { shot, info, msgs, dirs };
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_context != null) await _context.CloseAsync();
        _pw?.Dispose();
    }
}

/// <summary>
/// Keeps one client per session (number). Fake mode gives every session its own FakeWhatsAppClient; Real mode gives every session its own
/// browser profile folder (session.ProfileKey), so each number has its own login and none can disturb another.
/// </summary>
public class WhatsAppClientRegistry : IWhatsAppClientRegistry
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, IWhatsAppClient> _clients = new();
    private readonly Microsoft.Extensions.Configuration.IConfiguration _config;
    private readonly ILoggerFactory _loggers;
    private readonly object _lock = new();
    public event Action<Guid, IWhatsAppClient> ClientCreated;

    public WhatsAppClientRegistry(Microsoft.Extensions.Configuration.IConfiguration config, ILoggerFactory loggers) { _config = config; _loggers = loggers; }

    public IWhatsAppClient Get(Domain.WhatsAppSession session)
    {
        if (_clients.TryGetValue(session.Id, out var existing)) return existing;
        lock (_lock)
        {
            if (_clients.TryGetValue(session.Id, out existing)) return existing;
            IWhatsAppClient client = string.Equals(_config["WhatsApp:Client"], "Real", StringComparison.OrdinalIgnoreCase)
                ? new RealWhatsAppClient(_loggers.CreateLogger<RealWhatsAppClient>(), _config, session.ProfileKey)
                : new FakeWhatsAppClient(_loggers.CreateLogger<FakeWhatsAppClient>());
            _clients[session.Id] = client;
            ClientCreated?.Invoke(session.Id, client);
            return client;
        }
    }
}
