using Hangfire;
using Microsoft.EntityFrameworkCore;
using WhatsAppLab.Api;
using WhatsAppLab.Application;
using WhatsAppLab.Domain;
using WhatsAppLab.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var cs = builder.Configuration.GetConnectionString("DefaultConnection");

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddLabInfrastructure(builder.Configuration);

// Hangfire is what the ERP already uses for recurring jobs. Here it only creates the outbox row on schedule.
builder.Services.AddHangfire(c => c.SetDataCompatibilityLevel(CompatibilityLevel.Version_180).UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings().UseSqlServerStorage(cs));
builder.Services.AddHangfireServer();
builder.Services.AddScoped<AnnouncementJob>();
builder.Services.AddHostedService<WhatsAppDispatcherService>();   // sends the outbox and listens for incoming messages, for every number

var app = builder.Build();

if (app.Configuration.GetValue("Lab:AutoMigrate", true))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<LabDbContext>().Database.Migrate();
}
using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<IWhatsAppSessionUseCase>().EnsureDefaultAsync();   // first number + move old rows to it

// WhatsApp:AutoConnect = true brings the browser sessions back after a restart (the saved logins are reused, no new QR). Only matters for the Real client.
// It reconnects the first number and every number that was linked; a number that was never linked waits for someone to press Connect.
if (app.Configuration.GetValue<bool>("WhatsApp:AutoConnect"))
    app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
    {
        using var scope = app.Services.CreateScope();
        var sessions = await scope.ServiceProvider.GetRequiredService<IWhatsAppSessionUseCase>().ListAsync();
        var registry = app.Services.GetRequiredService<IWhatsAppClientRegistry>();
        foreach (var s in sessions.Where((s, i) => i == 0 || s.Status == (int)WhatsAppSessionStatus.Linked))
        {
            try { if (registry.Get(s) is IWhatsAppSessionControl c) await c.ConnectAsync(CancellationToken.None); }
            catch (Exception ex) { app.Logger.LogWarning(ex, "Auto connect failed for {Session}", s.Name); }
        }
    }));

app.UseDefaultFiles();
app.UseStaticFiles();   // the test dashboard: wwwroot/index.html
app.UseSwagger();
app.UseSwaggerUI();
app.UseHangfireDashboard("/hangfire");   // local requests only by default

var api = app.MapGroup("/api/whatsapp");

// Every endpoint below works on ONE number. Pass ?sessionId=... (or sessionId in the body); without it the first number is used,
// so a caller written for the single-number lab keeps working.
static async Task<(WhatsAppSession Session, IResult Error)> Sess(IWhatsAppSessionUseCase sessions, Guid? id)
{
    var s = await sessions.ResolveAsync(id);
    return s == null ? (null, Results.NotFound(new { error = "Unknown number (session)." })) : (s, null);
}

// ---- numbers (sessions)
api.MapGet("/sessions", async (IWhatsAppSessionUseCase sessions, IWhatsAppClientRegistry registry, IApplicationDbContext db, CancellationToken ct) =>
{
    var list = new List<object>();
    foreach (var s in await sessions.ListAsync())
    {
        var client = registry.Get(s);
        var link = await client.GetLinkStateAsync(ct);
        var setting = await db.WhatsAppSettingDbSet.AsNoTracking().FirstOrDefaultAsync(x => x.SessionId == s.Id, ct);
        list.Add(new
        {
            id = s.Id, name = s.Name, phone = link.PhoneNumber ?? s.PhoneNumber, linked = link.IsLinked,
            connection = (client as IWhatsAppSessionControl)?.Connection ?? "fake", enabled = setting?.IsEnabled ?? false,
            pending = await db.WhatsAppOutboundMessageDbSet.CountAsync(m => m.SessionId == s.Id && m.Status == (int)OutboundStatus.Pending, ct)
        });
    }
    return list;
});
api.MapPost("/sessions", async (NewSessionRequest r, IWhatsAppSessionUseCase sessions) =>
{
    var s = await sessions.CreateAsync(r.Name);
    return Results.Ok(new { id = s.Id, name = s.Name, note = "created, feature Off. Connect it and scan its QR, then switch it on." });
});

// ---- status and settings (settings row missing = Off)
api.MapGet("/status", async (Guid? sessionId, IWhatsAppSessionUseCase sessions, IWhatsAppClientRegistry registry, IWhatsAppSettingUseCase settings, IApplicationDbContext db, CancellationToken ct) =>
{
    var (session, err) = await Sess(sessions, sessionId); if (err != null) return err;
    var client = registry.Get(session);
    var link = await client.GetLinkStateAsync(ct);
    var s = await settings.GetAsync(session.Id);
    var mine = db.WhatsAppOutboundMessageDbSet.Where(m => m.SessionId == session.Id);
    return Results.Ok(new
    {
        sessionId = session.Id, name = session.Name,
        client = client.LibraryName, linked = link.IsLinked, phone = link.PhoneNumber, qr = link.QrCode,
        connection = (client as IWhatsAppSessionControl)?.Connection ?? "fake", canControl = client is IWhatsAppSessionControl,
        enabled = s?.IsEnabled ?? false,
        pending = await mine.CountAsync(m => m.Status == (int)OutboundStatus.Pending, ct),
        sent = await mine.CountAsync(m => m.Status == (int)OutboundStatus.Sent, ct),
        failed = await mine.CountAsync(m => m.Status == (int)OutboundStatus.Failed, ct)
    });
});
api.MapGet("/settings", async (Guid? sessionId, IWhatsAppSessionUseCase sessions, IWhatsAppSettingUseCase u) =>
{
    var (session, err) = await Sess(sessions, sessionId); if (err != null) return err;
    return Results.Ok((object)await u.GetAsync(session.Id) ?? new { enabled = false, note = "no row: feature is Off" });
});
api.MapPut("/settings", async (Guid? sessionId, WhatsAppSetting input, IWhatsAppSessionUseCase sessions, IWhatsAppSettingUseCase u) =>
{
    var (session, err) = await Sess(sessions, sessionId); if (err != null) return err;
    return Results.Ok(await u.SaveAsync(session.Id, input));
});

// ---- groups: name -> id
api.MapPost("/groups/sync", async (Guid? sessionId, IWhatsAppSessionUseCase sessions, IWhatsAppGroupUseCase u, CancellationToken ct) =>
{
    var (session, err) = await Sess(sessions, sessionId); if (err != null) return err;
    return Results.Ok(new { synced = await u.SyncAsync(session.Id, ct) });
});
api.MapGet("/groups", async (Guid? sessionId, IWhatsAppSessionUseCase sessions, IWhatsAppGroupUseCase u) =>
{
    var (session, err) = await Sess(sessions, sessionId); if (err != null) return err;
    return Results.Ok(await u.ListAsync(session.Id));
});

// the allow-list: which groups of this number may be sent to and read. Empty list = no restriction.
api.MapPut("/groups/allowed", async (Guid? sessionId, AllowedRequest r, IWhatsAppSessionUseCase sessions, IWhatsAppGroupUseCase u) =>
{
    var (session, err) = await Sess(sessions, sessionId); if (err != null) return err;
    return Results.Ok(new { allowed = await u.SetAllowedAsync(session.Id, r.GroupJids) });
});

// ---- send trigger 1: API (also what a button in the ERP would call)
api.MapPost("/messages", async (SendRequest r, IWhatsAppSessionUseCase sessions, IWhatsAppGroupUseCase groups, IWhatsAppOutboxUseCase outbox) =>
{
    var (session, err) = await Sess(sessions, r.SessionId); if (err != null) return err;
    var jid = await Resolver.ResolveJid(groups, session.Id, r.GroupName, r.GroupJid);
    if (jid == null) return Results.BadRequest(new { error = "Unknown group. Sync groups first or check the name." });
    if (r.MediaId == null && string.IsNullOrWhiteSpace(r.Body)) return Results.BadRequest(new { error = "Write a text, or attach a file (mediaId)." });
    try
    {
        var id = await outbox.EnqueueAsync(session.Id, jid, r.Body, r.ScheduledFor, OutboundSource.Api, "api", r.MediaId);
        return Results.Ok(new { id, sessionId = session.Id, note = "queued; the dispatcher sends it when it is due" });
    }
    catch (MediaRejectedException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (GroupNotAllowedException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// ---- media: upload a file once (audio, video, image), then send it with POST /messages {mediaId}; received files come back the same way
api.MapPost("/media", async (HttpRequest request, Guid? sessionId, IWhatsAppMediaUseCase media) =>
{
    if (!request.HasFormContentType) return Results.BadRequest(new { error = "Send the file as multipart/form-data, field name 'file'." });
    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null) return Results.BadRequest(new { error = "No file in the field 'file'." });
    try
    {
        await using var s = file.OpenReadStream();
        var m = await media.SaveAsync(s, file.FileName, file.ContentType, 0, sessionId);
        return Results.Ok(new { id = m.Id, kind = m.Kind, mime = m.MimeType, size = m.SizeBytes, sha256 = m.Sha256, name = m.FileName });
    }
    catch (MediaRejectedException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).DisableAntiforgery();
api.MapGet("/media/{id:guid}", async (Guid id, IWhatsAppMediaUseCase media) =>
{
    var m = await media.GetAsync(id);
    var path = m == null ? null : media.FullPath(m);
    return path != null && File.Exists(path) ? Results.File(path, m.MimeType, m.FileName) : Results.NotFound();
});
api.MapGet("/media/{id:guid}/info", async (Guid id, IWhatsAppMediaUseCase media) =>
{
    var m = await media.GetAsync(id);
    return m == null ? Results.NotFound() : Results.Ok(new { id = m.Id, kind = m.Kind, mime = m.MimeType, size = m.SizeBytes, sha256 = m.Sha256, name = m.FileName, direction = m.Direction == 0 ? "out" : "in" });
});
// shows what a template would turn into right now (nothing is queued)
api.MapPost("/messages/preview", (PreviewRequest r) => Results.Ok(new { text = MessageText.Render(r.Body) }));
api.MapGet("/messages", async (Guid? sessionId, IWhatsAppSessionUseCase sessions, IWhatsAppOutboxUseCase u) =>
{
    var (session, err) = await Sess(sessions, sessionId); if (err != null) return err;
    return Results.Ok(await u.ListAsync(session.Id, 50));
});

// ---- send trigger 3: schedule (Hangfire recurring job that writes to the outbox)
api.MapPost("/announcements", async (AnnouncementRequest r, IWhatsAppSessionUseCase sessions, IWhatsAppGroupUseCase groups) =>
{
    var (session, err) = await Sess(sessions, r.SessionId); if (err != null) return err;
    var jid = await Resolver.ResolveJid(groups, session.Id, r.GroupName, null);
    if (jid == null) return Results.BadRequest(new { error = "Unknown group." });
    var sid = session.Id;
    RecurringJob.AddOrUpdate<AnnouncementJob>(r.Id, j => j.Run(sid, jid, r.Body), r.Cron);
    return Results.Ok(new { r.Id, r.Cron, sessionId = sid });
});
api.MapDelete("/announcements/{id}", (string id) => { RecurringJob.RemoveIfExists(id); return Results.Ok(); });
api.MapPost("/announcements/{id}/run-now", (string id) => { RecurringJob.TriggerJob(id); return Results.Ok(); });   // for the lab: do not wait for the cron time

// ---- receiving
api.MapGet("/inbound", async (Guid? sessionId, IWhatsAppSessionUseCase sessions, IWhatsAppInboundUseCase u) =>
{
    var (session, err) = await Sess(sessions, sessionId); if (err != null) return err;
    return Results.Ok(await u.ListAsync(session.Id, 50));
});

// ---- login from the lab: connect opens WhatsApp Web (the QR shows up in /status), disconnect keeps the login, reset forgets it
async Task<IResult> Control(Guid? sessionId, IWhatsAppSessionUseCase sessions, IWhatsAppClientRegistry registry, Func<IWhatsAppSessionControl, Task> action)
{
    var (session, err) = await Sess(sessions, sessionId); if (err != null) return err;
    if (registry.Get(session) is not IWhatsAppSessionControl c) return Results.BadRequest("This client has no session");
    await action(c);
    return Results.Ok();
}
api.MapPost("/connection/connect", (Guid? sessionId, IWhatsAppSessionUseCase sessions, IWhatsAppClientRegistry registry, CancellationToken ct) => Control(sessionId, sessions, registry, c => c.ConnectAsync(ct)));
api.MapPost("/connection/disconnect", (Guid? sessionId, IWhatsAppSessionUseCase sessions, IWhatsAppClientRegistry registry, CancellationToken ct) => Control(sessionId, sessions, registry, c => c.DisconnectAsync(ct)));
api.MapPost("/connection/reset", (Guid? sessionId, IWhatsAppSessionUseCase sessions, IWhatsAppClientRegistry registry, CancellationToken ct) => Control(sessionId, sessions, registry, c => c.ResetSessionAsync(ct)));
api.MapGet("/real/debug", async (Guid? sessionId, IWhatsAppSessionUseCase sessions, IWhatsAppClientRegistry registry) =>
{
    var (session, err) = await Sess(sessions, sessionId); if (err != null) return err;
    return registry.Get(session) is RealWhatsAppClient r ? Results.Ok(await r.DebugAsync()) : Results.BadRequest();   // lab diagnostics for the Real client
});

// ---- lab-only helpers, available only with the Fake client
api.MapPost("/fake/incoming", async (IncomingRequest r, IWhatsAppSessionUseCase sessions, IWhatsAppClientRegistry registry, IWhatsAppGroupUseCase groups) =>
{
    var (session, err) = await Sess(sessions, r.SessionId); if (err != null) return err;
    if (registry.Get(session) is not FakeWhatsAppClient fake) return Results.BadRequest(new { error = "Only with the Fake client." });
    var jid = await Resolver.ResolveJid(groups, session.Id, r.GroupName, null);
    if (jid == null) return Results.BadRequest(new { error = "Unknown group." });
    await fake.SimulateIncomingAsync(jid, r.Sender, r.Body);
    return Results.Ok();
});
api.MapPost("/fake/incoming-media", async (IncomingMediaRequest r, IWhatsAppSessionUseCase sessions, IWhatsAppClientRegistry registry, IWhatsAppGroupUseCase groups) =>
{
    var (session, err) = await Sess(sessions, r.SessionId); if (err != null) return err;
    if (registry.Get(session) is not FakeWhatsAppClient fake) return Results.BadRequest(new { error = "Only with the Fake client." });
    var jid = await Resolver.ResolveJid(groups, session.Id, r.GroupName, null);
    if (jid == null) return Results.BadRequest(new { error = "Unknown group." });
    if (!File.Exists(r.FilePath)) return Results.BadRequest(new { error = "File not found." });
    await fake.SimulateIncomingMediaAsync(jid, r.Sender, r.Caption, r.FilePath, r.Mime);
    return Results.Ok();
});
api.MapGet("/fake/sent-media", async (Guid? sessionId, IWhatsAppSessionUseCase sessions, IWhatsAppClientRegistry registry) =>
{
    var (session, err) = await Sess(sessions, sessionId); if (err != null) return err;
    return registry.Get(session) is FakeWhatsAppClient f ? Results.Ok(f.SentMedia.Select(s => new { jid = s.Jid, file = s.FileName, mime = s.Mime, caption = s.Caption, sha256 = s.Sha256, size = s.Size })) : Results.BadRequest();
});
api.MapPost("/fake/fail-next", async (Guid? sessionId, IWhatsAppSessionUseCase sessions, IWhatsAppClientRegistry registry) =>
{
    var (session, err) = await Sess(sessions, sessionId); if (err != null) return err;
    if (registry.Get(session) is FakeWhatsAppClient f) f.FailNextSend();
    return Results.Ok();
});
api.MapGet("/fake/sent", async (Guid? sessionId, IWhatsAppSessionUseCase sessions, IWhatsAppClientRegistry registry) =>
{
    var (session, err) = await Sess(sessions, sessionId); if (err != null) return err;
    return registry.Get(session) is FakeWhatsAppClient f ? Results.Ok(f.Sent.Select(s => new { jid = s.Jid, body = s.Body, at = s.At })) : Results.BadRequest();
});

app.Run();

public record NewSessionRequest(string Name);
public record PreviewRequest(string Body);
public record AllowedRequest(string[] GroupJids);
public record SendRequest(string GroupName, string GroupJid, string Body, DateTime? ScheduledFor, Guid? SessionId = null, Guid? MediaId = null);
public record AnnouncementRequest(string Id, string GroupName, string Body, string Cron, Guid? SessionId = null);
public record IncomingRequest(string GroupName, string Sender, string Body, Guid? SessionId = null);
public record IncomingMediaRequest(string GroupName, string Sender, string Caption, string FilePath, string Mime, Guid? SessionId = null);

public static class Resolver
{
    public static async Task<string> ResolveJid(IWhatsAppGroupUseCase groups, Guid sessionId, string name, string jid)
    {
        if (!string.IsNullOrWhiteSpace(jid)) return jid;
        var all = await groups.ListAsync(sessionId);
        return all.FirstOrDefault(g => g.IsActive && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))?.GroupJid;
    }
}
