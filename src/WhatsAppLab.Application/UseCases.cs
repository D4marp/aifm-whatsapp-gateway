using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppLab.Domain;

namespace WhatsAppLab.Application;

// Same shape as the ERP use cases (Application/UseCases/...): constructor takes IApplicationDbContext, methods return plain results.

public class WhatsAppSettingUseCase : IWhatsAppSettingUseCase
{
    private readonly IApplicationDbContext _db;
    public WhatsAppSettingUseCase(IApplicationDbContext db) { _db = db; }

    // No row means Off, like the face feature in the ERP. Reading must never throw: a missing table counts as Off.
    public async Task<WhatsAppSetting> GetAsync()
    {
        try { return await _db.WhatsAppSettingDbSet.AsNoTracking().FirstOrDefaultAsync(); }
        catch { return null; }
    }

    public async Task<WhatsAppSetting> SaveAsync(WhatsAppSetting input)
    {
        var row = await _db.WhatsAppSettingDbSet.FirstOrDefaultAsync();
        if (row == null) { row = new WhatsAppSetting { Id = Guid.NewGuid() }; _db.WhatsAppSettingDbSet.Add(row); }
        row.IsEnabled = input.IsEnabled;
        row.MaxMessagesPerDay = input.MaxMessagesPerDay;
        row.MinSecondsBetweenMessages = Math.Max(0, input.MinSecondsBetweenMessages);
        row.ReceiveEnabled = input.ReceiveEnabled;
        row.InboundRetentionDays = input.InboundRetentionDays;
        row.SendFromHour = input.SendFromHour; row.SendToHour = input.SendToHour;
        row.MaxPerWindow = input.MaxPerWindow; row.WindowMinutes = input.WindowMinutes;
        row.JitterSeconds = Math.Max(0, input.JitterSeconds);
        await _db.SaveChangesAsync();
        return row;
    }
}

public class WhatsAppGroupUseCase : IWhatsAppGroupUseCase
{
    private readonly IApplicationDbContext _db;
    private readonly IWhatsAppClient _client;
    public WhatsAppGroupUseCase(IApplicationDbContext db, IWhatsAppClient client) { _db = db; _client = client; }

    /// <summary>Copies the groups the linked number has joined into the table, so a group NAME can be turned into a group ID.</summary>
    public async Task<int> SyncAsync(CancellationToken ct)
    {
        var state = await _client.GetLinkStateAsync(ct);
        if (!state.IsLinked) return 0;

        var session = await _db.WhatsAppSessionDbSet.FirstOrDefaultAsync(ct);
        if (session == null) { session = new WhatsAppSession { Id = Guid.NewGuid() }; _db.WhatsAppSessionDbSet.Add(session); }
        session.PhoneNumber = state.PhoneNumber;
        session.Status = (int)WhatsAppSessionStatus.Linked;
        session.LinkedOn ??= DateTime.UtcNow;
        session.LastSeenOn = DateTime.UtcNow;
        session.LibraryName = _client.LibraryName;

        var live = await _client.ListGroupsAsync(ct);
        var existing = await _db.WhatsAppGroupDbSet.ToListAsync(ct);
        foreach (var g in live)
        {
            var row = existing.FirstOrDefault(x => x.GroupJid == g.Jid);
            if (row == null) { row = new WhatsAppGroup { Id = Guid.NewGuid(), SessionId = session.Id, GroupJid = g.Jid }; _db.WhatsAppGroupDbSet.Add(row); }
            row.Name = g.Name; row.ParticipantCount = g.ParticipantCount; row.LastSyncedOn = DateTime.UtcNow; row.IsActive = true;
        }
        foreach (var gone in existing.Where(x => live.All(l => l.Jid != x.GroupJid))) gone.IsActive = false;
        await _db.SaveChangesAsync(ct);
        return live.Count;
    }

    public Task<List<WhatsAppGroup>> ListAsync() => _db.WhatsAppGroupDbSet.AsNoTracking().OrderBy(g => g.Name).ToListAsync();
}

public class WhatsAppOutboxUseCase : IWhatsAppOutboxUseCase
{
    private const int MaxAttempts = 3;
    private static DateTime _nextAllowedUtc = DateTime.MinValue;   // the random part of the gap after the last send (kept in memory: after a restart the minimum gap from the database still applies)
    private readonly IApplicationDbContext _db;
    private readonly IWhatsAppClient _client;
    private readonly ILogger<WhatsAppOutboxUseCase> _logger;
    public WhatsAppOutboxUseCase(IApplicationDbContext db, IWhatsAppClient client, ILogger<WhatsAppOutboxUseCase> logger) { _db = db; _client = client; _logger = logger; }

    public async Task<Guid> EnqueueAsync(string groupJid, string body, DateTime? scheduledFor, OutboundSource source, string sourceReference)
    {
        var row = new WhatsAppOutboundMessage
        {
            Id = Guid.NewGuid(), GroupJid = groupJid, Body = body,
            ScheduledFor = scheduledFor ?? DateTime.UtcNow, Status = (int)OutboundStatus.Pending,
            Source = (int)source, SourceReference = sourceReference, CorrelationId = Guid.NewGuid().ToString("N")
        };
        _db.WhatsAppOutboundMessageDbSet.Add(row);
        await _db.SaveChangesAsync();
        return row.Id;
    }

    public async Task<int> DispatchDueAsync(CancellationToken ct)
    {
        var sent = 0;
        try
        {
            var setting = await _db.WhatsAppSettingDbSet.AsNoTracking().FirstOrDefaultAsync(ct);
            if (setting == null || !setting.IsEnabled) return 0;                       // Off: nothing is sent, rows stay Pending

            var state = await _client.GetLinkStateAsync(ct);
            if (!state.IsLinked) return 0;                                              // not linked: keep everything Pending

            // sending hours (server local time): outside them everything waits
            if (setting.SendFromHour.HasValue && setting.SendToHour.HasValue)
            {
                var h = DateTime.Now.Hour;
                var inside = setting.SendFromHour <= setting.SendToHour ? (h >= setting.SendFromHour && h < setting.SendToHour) : (h >= setting.SendFromHour || h < setting.SendToHour);
                if (!inside) return 0;
            }

            var today = DateTime.UtcNow.Date;
            var sentToday = await _db.WhatsAppOutboundMessageDbSet.CountAsync(m => m.Status == (int)OutboundStatus.Sent && m.SentOn >= today, ct);
            var lastSent = await _db.WhatsAppOutboundMessageDbSet.Where(m => m.Status == (int)OutboundStatus.Sent).MaxAsync(m => (DateTime?)m.SentOn, ct);

            var due = await _db.WhatsAppOutboundMessageDbSet
                .Where(m => m.Status == (int)OutboundStatus.Pending && m.ScheduledFor <= DateTime.UtcNow)
                .OrderBy(m => m.ScheduledFor).Take(20).ToListAsync(ct);

            // wave limit: at most MaxPerWindow messages inside the last WindowMinutes
            var sentInWindow = 0;
            if (setting.MaxPerWindow.HasValue && setting.WindowMinutes.HasValue)
            {
                var since = DateTime.UtcNow.AddMinutes(-setting.WindowMinutes.Value);
                sentInWindow = await _db.WhatsAppOutboundMessageDbSet.CountAsync(m => m.Status == (int)OutboundStatus.Sent && m.SentOn >= since, ct);
            }

            foreach (var m in due)
            {
                if (setting.MaxMessagesPerDay.HasValue && sentToday >= setting.MaxMessagesPerDay.Value) break;          // daily cap reached
                if (setting.MaxPerWindow.HasValue && setting.WindowMinutes.HasValue && sentInWindow >= setting.MaxPerWindow.Value) break;   // this wave is full
                if (lastSent.HasValue && (DateTime.UtcNow - lastSent.Value).TotalSeconds < setting.MinSecondsBetweenMessages) break; // too soon, try next tick
                if (DateTime.UtcNow < _nextAllowedUtc) break;                                                                // random jitter gap not over yet

                m.Attempts++;
                SendResult result;
                try { result = await _client.SendGroupMessageAsync(m.GroupJid, m.Body, ct); }
                catch (Exception ex) { result = new SendResult(false, ex.Message); }   // one failing message must not stop the loop (fail-open)

                if (result.Success) { m.Status = (int)OutboundStatus.Sent; m.SentOn = DateTime.UtcNow; m.ErrorMessage = null; sentToday++; sentInWindow++; sent++; lastSent = m.SentOn; _nextAllowedUtc = DateTime.UtcNow.AddSeconds(Random.Shared.Next(0, setting.JitterSeconds + 1)); }
                else
                {
                    // a real client can return a very long error (a browser call log); the column holds 1000 characters, and a too long value made the save fail and the row stay Pending forever
                    m.ErrorMessage = result.Error != null && result.Error.Length > 1000 ? result.Error.Substring(0, 1000) : result.Error;
                    if (m.Attempts >= MaxAttempts) m.Status = (int)OutboundStatus.Failed;
                    else m.ScheduledFor = DateTime.UtcNow.AddSeconds(10 * m.Attempts);        // simple back-off, then Pending again
                }
                await _db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "WhatsApp dispatch failed. Nothing else is affected."); }
        return sent;
    }

    public Task<List<WhatsAppOutboundMessage>> ListAsync(int take) => _db.WhatsAppOutboundMessageDbSet.AsNoTracking().OrderByDescending(m => m.CreatedOn).Take(take).ToListAsync();
}

public class WhatsAppInboundUseCase : IWhatsAppInboundUseCase
{
    private readonly IApplicationDbContext _db;
    private readonly ILogger<WhatsAppInboundUseCase> _logger;
    public WhatsAppInboundUseCase(IApplicationDbContext db, ILogger<WhatsAppInboundUseCase> logger) { _db = db; _logger = logger; }

    public async Task RecordAsync(InboundMessage message)
    {
        try
        {
            var setting = await _db.WhatsAppSettingDbSet.AsNoTracking().FirstOrDefaultAsync();
            if (setting == null || !setting.IsEnabled || !setting.ReceiveEnabled) return;

            // only groups we know about are stored (privacy: do not keep chats from groups nobody asked for)
            var known = await _db.WhatsAppGroupDbSet.AnyAsync(g => g.GroupJid == message.GroupJid && g.IsActive);
            if (!known) return;

            // "processing" is the part you own: read the text, decide, act. The lab only marks it processed.
            _db.WhatsAppInboundMessageDbSet.Add(new WhatsAppInboundMessage
            {
                Id = Guid.NewGuid(), GroupJid = message.GroupJid, SenderJid = message.SenderJid, SenderName = message.SenderName,
                Body = message.Body, MessageType = "text", ReceivedOn = message.ReceivedOn,
                ProcessedOn = DateTime.UtcNow, ProcessStatus = (int)InboundProcessStatus.Processed
            });
            await _db.SaveChangesAsync();
        }
        catch (Exception ex) { _logger.LogError(ex, "Could not record the received WhatsApp message."); }
    }

    public Task<List<WhatsAppInboundMessage>> ListAsync(int take) => _db.WhatsAppInboundMessageDbSet.AsNoTracking().OrderByDescending(m => m.ReceivedOn).Take(take).ToListAsync();
}
