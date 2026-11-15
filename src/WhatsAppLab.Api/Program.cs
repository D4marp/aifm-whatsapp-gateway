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
builder.Services.AddHostedService<WhatsAppDispatcherService>();   // sends the outbox and listens for incoming messages

var app = builder.Build();

if (app.Configuration.GetValue("Lab:AutoMigrate", true))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<LabDbContext>().Database.Migrate();
}

// WhatsApp:AutoConnect = true brings the browser session back after a restart (the saved login is reused, no new QR). Only matters for the Real client.
if (app.Configuration.GetValue<bool>("WhatsApp:AutoConnect"))
    app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
    {
        try { if (app.Services.GetRequiredService<IWhatsAppClient>() is IWhatsAppSessionControl c) await c.ConnectAsync(CancellationToken.None); }
        catch (Exception ex) { app.Logger.LogWarning(ex, "Auto connect failed"); }
    }));

app.UseDefaultFiles();
app.UseStaticFiles();   // the test dashboard: wwwroot/index.html
app.UseSwagger();
app.UseSwaggerUI();
app.UseHangfireDashboard("/hangfire");   // local requests only by default

var api = app.MapGroup("/api/whatsapp");

// ---- status and settings (settings row missing = Off)
api.MapGet("/status", async (IWhatsAppClient client, IWhatsAppSettingUseCase settings, IApplicationDbContext db, CancellationToken ct) =>
{
    var link = await client.GetLinkStateAsync(ct);
    var s = await settings.GetAsync();
    return new
    {
        client = client.LibraryName, linked = link.IsLinked, phone = link.PhoneNumber, qr = link.QrCode,
        connection = (client as IWhatsAppSessionControl)?.Connection ?? "fake", canControl = client is IWhatsAppSessionControl,
        enabled = s?.IsEnabled ?? false,
        pending = await db.WhatsAppOutboundMessageDbSet.CountAsync(m => m.Status == (int)OutboundStatus.Pending, ct),
        sent = await db.WhatsAppOutboundMessageDbSet.CountAsync(m => m.Status == (int)OutboundStatus.Sent, ct),
        failed = await db.WhatsAppOutboundMessageDbSet.CountAsync(m => m.Status == (int)OutboundStatus.Failed, ct)
    };
});
api.MapGet("/settings", async (IWhatsAppSettingUseCase u) => (object)await u.GetAsync() ?? new { enabled = false, note = "no row: feature is Off" });
api.MapPut("/settings", async (WhatsAppSetting input, IWhatsAppSettingUseCase u) => await u.SaveAsync(input));

// ---- groups: name -> id
api.MapPost("/groups/sync", async (IWhatsAppGroupUseCase u, CancellationToken ct) => new { synced = await u.SyncAsync(ct) });
api.MapGet("/groups", async (IWhatsAppGroupUseCase u) => await u.ListAsync());

// ---- send trigger 1: API (also what a button in the ERP would call)
api.MapPost("/messages", async (SendRequest r, IWhatsAppGroupUseCase groups, IWhatsAppOutboxUseCase outbox) =>
{
    var jid = await Resolver.ResolveJid(groups, r.GroupName, r.GroupJid);
    if (jid == null) return Results.BadRequest(new { error = "Unknown group. Sync groups first or check the name." });
    var id = await outbox.EnqueueAsync(jid, r.Body, r.ScheduledFor, OutboundSource.Api, "api");
    return Results.Ok(new { id, note = "queued; the dispatcher sends it when it is due" });
});
api.MapGet("/messages", async (IWhatsAppOutboxUseCase u) => await u.ListAsync(50));

// ---- send trigger 3: schedule (Hangfire recurring job that writes to the outbox)
api.MapPost("/announcements", async (AnnouncementRequest r, IWhatsAppGroupUseCase groups) =>
{
    var jid = await Resolver.ResolveJid(groups, r.GroupName, null);
    if (jid == null) return Results.BadRequest(new { error = "Unknown group." });
    RecurringJob.AddOrUpdate<AnnouncementJob>(r.Id, j => j.Run(jid, r.Body), r.Cron);
    return Results.Ok(new { r.Id, r.Cron });
});
api.MapDelete("/announcements/{id}", (string id) => { RecurringJob.RemoveIfExists(id); return Results.Ok(); });
api.MapPost("/announcements/{id}/run-now", (string id) => { RecurringJob.TriggerJob(id); return Results.Ok(); });   // for the lab: do not wait for the cron time

// ---- receiving
api.MapGet("/inbound", async (IWhatsAppInboundUseCase u) => await u.ListAsync(50));

// ---- lab-only helpers, available only with the Fake client
api.MapPost("/fake/incoming", async (IncomingRequest r, IWhatsAppClient client, IWhatsAppGroupUseCase groups) =>
{
    if (client is not FakeWhatsAppClient fake) return Results.BadRequest(new { error = "Only with the Fake client." });
    var jid = await Resolver.ResolveJid(groups, r.GroupName, null);
    if (jid == null) return Results.BadRequest(new { error = "Unknown group." });
    await fake.SimulateIncomingAsync(jid, r.Sender, r.Body);
    return Results.Ok();
});
// ---- login from the lab: connect opens WhatsApp Web (the QR shows up in /status), disconnect keeps the login, reset forgets it
api.MapPost("/connection/connect", async (IWhatsAppClient client, CancellationToken ct) => { if (client is not IWhatsAppSessionControl c) return Results.BadRequest("This client has no session"); await c.ConnectAsync(ct); return Results.Ok(); });
api.MapPost("/connection/disconnect", async (IWhatsAppClient client, CancellationToken ct) => { if (client is not IWhatsAppSessionControl c) return Results.BadRequest("This client has no session"); await c.DisconnectAsync(ct); return Results.Ok(); });
api.MapPost("/connection/reset", async (IWhatsAppClient client, CancellationToken ct) => { if (client is not IWhatsAppSessionControl c) return Results.BadRequest("This client has no session"); await c.ResetSessionAsync(ct); return Results.Ok(); });
api.MapGet("/real/debug", async (IWhatsAppClient client) => client is RealWhatsAppClient r ? Results.Ok(await r.DebugAsync()) : Results.BadRequest());   // lab diagnostics for the Real client
api.MapPost("/fake/fail-next", (IWhatsAppClient client) => { if (client is FakeWhatsAppClient f) f.FailNextSend(); return Results.Ok(); });
api.MapGet("/fake/sent", (IWhatsAppClient client) => client is FakeWhatsAppClient f ? Results.Ok(f.Sent.Select(s => new { jid = s.Jid, body = s.Body, at = s.At })) : Results.BadRequest());

app.Run();

public record SendRequest(string GroupName, string GroupJid, string Body, DateTime? ScheduledFor);
public record AnnouncementRequest(string Id, string GroupName, string Body, string Cron);
public record IncomingRequest(string GroupName, string Sender, string Body);

public static class Resolver
{
    public static async Task<string> ResolveJid(IWhatsAppGroupUseCase groups, string name, string jid)
    {
        if (!string.IsNullOrWhiteSpace(jid)) return jid;
        var all = await groups.ListAsync();
        return all.FirstOrDefault(g => g.IsActive && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))?.GroupJid;
    }
}
