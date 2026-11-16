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
    private static string ProfileDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "aifm-whatsapp-lab", "session", "edge-profile");
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
        await _gate.WaitAsync(ct);
        try { await CloseBrowserAsync(); }
        finally { _gate.Release(); }
    }

    public async Task ResetSessionAsync(CancellationToken ct)
    {
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
    public RealWhatsAppClient(ILogger<RealWhatsAppClient> logger, Microsoft.Extensions.Configuration.IConfiguration config)
    {
        _logger = logger;
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
                var known = rows.Where(r => r.title != null && _groupTitles.Contains(r.title)).ToList();
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
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "aifm-whatsapp-lab", "session", "edge-profile");
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
            await ClearSearchAsync(p);   // empty the search so the chat list is back to normal (and shows unread badges)
            _logger.LogInformation("[REAL WhatsApp] sent to {Name}", name);
            return new SendResult(true, null);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Send failed"); return new SendResult(false, ex.Message); }
        finally { _gate.Release(); }
    }

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
