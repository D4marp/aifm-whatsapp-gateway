using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppLab.Domain;

namespace WhatsAppLab.Application;

// Same shape as the ERP use cases (Application/UseCases/...): constructor takes IApplicationDbContext, methods return plain results.
// Since the multi-number change every use case works on ONE session (number); the caller resolves which one (IWhatsAppSessionUseCase.ResolveAsync).

public class WhatsAppSessionUseCase : IWhatsAppSessionUseCase
{
    private readonly IApplicationDbContext _db;
    public WhatsAppSessionUseCase(IApplicationDbContext db) { _db = db; }

    public async Task EnsureDefaultAsync()
    {
        var sessions = await _db.WhatsAppSessionDbSet.OrderBy(s => s.CreatedOn).ToListAsync();
        if (sessions.Count == 0)
        {
            sessions.Add(new WhatsAppSession { Id = Guid.NewGuid(), Name = "Default", ProfileKey = "edge-profile", Status = (int)WhatsAppSessionStatus.NotLinked, IsActive = true });
            _db.WhatsAppSessionDbSet.Add(sessions[0]);
        }
        for (var i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            if (string.IsNullOrWhiteSpace(s.ProfileKey)) { s.ProfileKey = i == 0 ? "edge-profile" : "session-" + s.Id.ToString("N").Substring(0, 8); s.IsActive = true; }   // an old row got IsActive = false from the migration default
            if (string.IsNullOrWhiteSpace(s.Name)) s.Name = i == 0 ? "Default" : "Number " + (i + 1);
        }
        await _db.SaveChangesAsync();

        // rows from before several numbers existed belong to the first session
        var first = sessions[0].Id;
        foreach (var m in await _db.WhatsAppOutboundMessageDbSet.Where(m => m.SessionId == null).ToListAsync()) m.SessionId = first;
        foreach (var m in await _db.WhatsAppInboundMessageDbSet.Where(m => m.SessionId == null).ToListAsync()) m.SessionId = first;
        foreach (var m in await _db.WhatsAppSettingDbSet.Where(m => m.SessionId == null).ToListAsync()) m.SessionId = first;
        await _db.SaveChangesAsync();
    }

    public Task<List<WhatsAppSession>> ListAsync() => _db.WhatsAppSessionDbSet.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.CreatedOn).ToListAsync();

    public async Task<WhatsAppSession> ResolveAsync(Guid? sessionId)
    {
        var q = _db.WhatsAppSessionDbSet.AsNoTracking().Where(s => s.IsActive);
        return sessionId.HasValue ? await q.FirstOrDefaultAsync(s => s.Id == sessionId.Value) : await q.OrderBy(s => s.CreatedOn).FirstOrDefaultAsync();
    }

    public async Task<WhatsAppSession> CreateAsync(string name)
    {
        var id = Guid.NewGuid();
        var s = new WhatsAppSession { Id = id, Name = string.IsNullOrWhiteSpace(name) ? "New number" : name.Trim(), ProfileKey = "session-" + id.ToString("N").Substring(0, 8), Status = (int)WhatsAppSessionStatus.NotLinked, IsActive = true };
        _db.WhatsAppSessionDbSet.Add(s);
        await _db.SaveChangesAsync();   // no settings row on purpose: a new number starts Off until someone switches it on
        return s;
    }
}

public class WhatsAppSettingUseCase : IWhatsAppSettingUseCase
{
    private readonly IApplicationDbContext _db;
    public WhatsAppSettingUseCase(IApplicationDbContext db) { _db = db; }

    // No row means Off, like the face feature in the ERP. Reading must never throw: a missing table counts as Off.
    public async Task<WhatsAppSetting> GetAsync(Guid sessionId)
    {
        try { return await _db.WhatsAppSettingDbSet.AsNoTracking().FirstOrDefaultAsync(s => s.SessionId == sessionId); }
        catch { return null; }
    }

    public async Task<WhatsAppSetting> SaveAsync(Guid sessionId, WhatsAppSetting input)
    {
        var row = await _db.WhatsAppSettingDbSet.FirstOrDefaultAsync(s => s.SessionId == sessionId);
        if (row == null) { row = new WhatsAppSetting { Id = Guid.NewGuid(), SessionId = sessionId }; _db.WhatsAppSettingDbSet.Add(row); }
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
    private readonly IWhatsAppClientRegistry _clients;
    public WhatsAppGroupUseCase(IApplicationDbContext db, IWhatsAppClientRegistry clients) { _db = db; _clients = clients; }

    /// <summary>Copies the groups this number has joined into the table, so a group NAME can be turned into a group ID.</summary>
    public async Task<int> SyncAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await _db.WhatsAppSessionDbSet.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session == null) return 0;
        var client = _clients.Get(session);
        var state = await client.GetLinkStateAsync(ct);
        if (!state.IsLinked) return 0;

        session.PhoneNumber = state.PhoneNumber;
        session.Status = (int)WhatsAppSessionStatus.Linked;
        session.LinkedOn ??= DateTime.UtcNow;
        session.LastSeenOn = DateTime.UtcNow;
        session.LibraryName = client.LibraryName;

        var live = await client.ListGroupsAsync(ct);
        var existing = await _db.WhatsAppGroupDbSet.Where(x => x.SessionId == sessionId).ToListAsync(ct);
        foreach (var g in live)
        {
            var row = existing.FirstOrDefault(x => x.GroupJid == g.Jid);
            if (row == null) { row = new WhatsAppGroup { Id = Guid.NewGuid(), SessionId = sessionId, GroupJid = g.Jid }; _db.WhatsAppGroupDbSet.Add(row); }
            row.Name = g.Name; row.ParticipantCount = g.ParticipantCount; row.LastSyncedOn = DateTime.UtcNow; row.IsActive = true;
        }
        foreach (var gone in existing.Where(x => live.All(l => l.Jid != x.GroupJid))) gone.IsActive = false;
        await _db.SaveChangesAsync(ct);
        return live.Count;
    }

    public Task<List<WhatsAppGroup>> ListAsync(Guid sessionId) => _db.WhatsAppGroupDbSet.AsNoTracking().Where(g => g.SessionId == sessionId).OrderBy(g => g.Name).ToListAsync();

    public async Task<int> SetAllowedAsync(Guid sessionId, IEnumerable<string> groupJids)
    {
        var wanted = new HashSet<string>(groupJids ?? Array.Empty<string>());
        var rows = await _db.WhatsAppGroupDbSet.Where(g => g.SessionId == sessionId).ToListAsync();
        foreach (var g in rows) g.IsAllowed = wanted.Contains(g.GroupJid);
        await _db.SaveChangesAsync();
        return rows.Count(g => g.IsAllowed);
    }

    public Task<List<string>> AllowedNamesAsync(Guid sessionId) =>
        _db.WhatsAppGroupDbSet.AsNoTracking().Where(g => g.SessionId == sessionId && g.IsAllowed && g.IsActive).Select(g => g.Name).ToListAsync();
}

public class GroupNotAllowedException : Exception { public GroupNotAllowedException(string message) : base(message) { } }

public class MediaOptions
{
    /// <summary>Folder for the files. Outside the repo on purpose: messages and media can hold personal data.</summary>
    public string Root { get; set; }
    /// <summary>Our own upper limit per file. It is not WhatsApp's limit (that still has to be verified in Phase 0); raise or lower it after the tests.</summary>
    public long MaxBytes { get; set; } = 25L * 1024 * 1024;
}

public class WhatsAppMediaUseCase : IWhatsAppMediaUseCase
{
    // Only audio, video and images for now. The kind comes from the extension first (browsers often send a generic type), then from the MIME prefix.
    private static readonly Dictionary<string, (string Kind, string Mime)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp3"] = ("audio", "audio/mpeg"), [".m4a"] = ("audio", "audio/mp4"), [".aac"] = ("audio", "audio/aac"), [".ogg"] = ("audio", "audio/ogg"),
        [".opus"] = ("audio", "audio/ogg"), [".wav"] = ("audio", "audio/wav"), [".amr"] = ("audio", "audio/amr"),
        [".mp4"] = ("video", "video/mp4"), [".mov"] = ("video", "video/quicktime"), [".webm"] = ("video", "video/webm"), [".3gp"] = ("video", "video/3gpp"), [".mkv"] = ("video", "video/x-matroska"),
        [".jpg"] = ("image", "image/jpeg"), [".jpeg"] = ("image", "image/jpeg"), [".png"] = ("image", "image/png"), [".webp"] = ("image", "image/webp"), [".gif"] = ("image", "image/gif")
    };

    private readonly IApplicationDbContext _db;
    private readonly MediaOptions _options;
    public WhatsAppMediaUseCase(IApplicationDbContext db, MediaOptions options) { _db = db; _options = options; }

    public async Task<WhatsAppMedia> SaveAsync(Stream content, string fileName, string mimeType, int direction, Guid? sessionId)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        string kind, mime;
        if (Known.TryGetValue(ext, out var k)) { kind = k.Kind; mime = string.IsNullOrWhiteSpace(mimeType) || mimeType == "application/octet-stream" ? k.Mime : mimeType; }
        else if (!string.IsNullOrWhiteSpace(mimeType) && (mimeType.StartsWith("audio/") || mimeType.StartsWith("video/") || mimeType.StartsWith("image/")))
        { kind = mimeType.Substring(0, mimeType.IndexOf('/')); mime = mimeType; if (ext.Length == 0 || ext.Length > 6) ext = ""; }
        else throw new MediaRejectedException("File type not allowed. Only audio, video and image files are accepted.");

        var root = _options.Root;
        var incoming = Path.Combine(root, "incoming"); Directory.CreateDirectory(incoming);
        var temp = Path.Combine(incoming, Guid.NewGuid().ToString("N") + ".part");
        long size = 0;
        string sha;
        try
        {
            using (var hash = System.Security.Cryptography.SHA256.Create())
            using (var outFile = File.Create(temp))
            {
                var buffer = new byte[81920]; int n;
                while ((n = await content.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    size += n;
                    if (size > _options.MaxBytes) throw new MediaRejectedException($"File is larger than the limit ({_options.MaxBytes / (1024 * 1024)} MB).");
                    hash.TransformBlock(buffer, 0, n, null, 0);
                    await outFile.WriteAsync(buffer, 0, n);
                }
                hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                sha = Convert.ToHexString(hash.Hash).ToLowerInvariant();
            }
            if (size == 0) throw new MediaRejectedException("The file is empty.");
        }
        catch { try { File.Delete(temp); } catch { } throw; }

        var id = Guid.NewGuid();
        var relative = Path.Combine(DateTime.UtcNow.ToString("yyyy"), DateTime.UtcNow.ToString("MM"), id.ToString("N") + ext);
        var full = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full));
        File.Move(temp, full);

        var row = new WhatsAppMedia
        {
            Id = id, SessionId = sessionId, Direction = direction, Kind = kind, MimeType = mime,
            FileName = Path.GetFileName(fileName ?? id.ToString("N") + ext), SizeBytes = size, Sha256 = sha, StoragePath = relative
        };
        _db.WhatsAppMediaDbSet.Add(row);
        await _db.SaveChangesAsync();
        return row;
    }

    public Task<WhatsAppMedia> GetAsync(Guid id) => _db.WhatsAppMediaDbSet.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id);
    public string FullPath(WhatsAppMedia media) => Path.Combine(_options.Root, media.StoragePath);
}

public class WhatsAppOutboxUseCase : IWhatsAppOutboxUseCase
{
    private const int MaxAttempts = 3;
    // the random part of the gap after the last send, per number (kept in memory: after a restart the minimum gap from the database still applies)
    private static readonly ConcurrentDictionary<Guid, DateTime> _nextAllowedUtc = new();
    private readonly IApplicationDbContext _db;
    private readonly IWhatsAppClientRegistry _clients;
    private readonly IWhatsAppMediaUseCase _media;
    private readonly ILogger<WhatsAppOutboxUseCase> _logger;
    public WhatsAppOutboxUseCase(IApplicationDbContext db, IWhatsAppClientRegistry clients, IWhatsAppMediaUseCase media, ILogger<WhatsAppOutboxUseCase> logger) { _db = db; _clients = clients; _media = media; _logger = logger; }

    public async Task<Guid> EnqueueAsync(Guid sessionId, string groupJid, string body, DateTime? scheduledFor, OutboundSource source, string sourceReference, Guid? mediaId = null)
    {
        // allow-list: once any group of this number is allowed, only allowed groups can be queued
        var allowed = await _db.WhatsAppGroupDbSet.AsNoTracking().Where(g => g.SessionId == sessionId && g.IsAllowed && g.IsActive).Select(g => g.GroupJid).ToListAsync();
        if (allowed.Count > 0 && !allowed.Contains(groupJid)) throw new GroupNotAllowedException("This group is not on the allowed list of this number.");

        string type = "text";
        if (mediaId.HasValue)
        {
            var media = await _media.GetAsync(mediaId.Value);
            if (media == null) throw new MediaRejectedException("Unknown media id.");
            type = media.Kind;
        }
        var row = new WhatsAppOutboundMessage
        {
            MediaId = mediaId, MessageType = type,
            Id = Guid.NewGuid(), SessionId = sessionId, GroupJid = groupJid, Body = MessageText.Render(body ?? ""),   // {time}, {rand}, {a|b} ... become the real text here
            ScheduledFor = scheduledFor ?? DateTime.UtcNow, Status = (int)OutboundStatus.Pending,
            Source = (int)source, SourceReference = sourceReference, CorrelationId = Guid.NewGuid().ToString("N")
        };
        _db.WhatsAppOutboundMessageDbSet.Add(row);
        await _db.SaveChangesAsync();
        return row.Id;
    }

    public async Task<int> DispatchDueAsync(Guid sessionId, CancellationToken ct)
    {
        var sent = 0;
        try
        {
            var setting = await _db.WhatsAppSettingDbSet.AsNoTracking().FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);
            if (setting == null || !setting.IsEnabled) return 0;                       // Off: nothing is sent, rows stay Pending

            var session = await _db.WhatsAppSessionDbSet.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId && s.IsActive, ct);
            if (session == null) return 0;
            var client = _clients.Get(session);
            var state = await client.GetLinkStateAsync(ct);
            if (!state.IsLinked) return 0;                                              // not linked: keep everything Pending

            // sending hours (server local time): outside them everything waits
            if (setting.SendFromHour.HasValue && setting.SendToHour.HasValue)
            {
                var h = DateTime.Now.Hour;
                var inside = setting.SendFromHour <= setting.SendToHour ? (h >= setting.SendFromHour && h < setting.SendToHour) : (h >= setting.SendFromHour || h < setting.SendToHour);
                if (!inside) return 0;
            }

            // A row written straight into the table by SQL Server may leave SessionId empty: it then belongs to the first number. To target another number, set SessionId in the insert.
            var firstId = await _db.WhatsAppSessionDbSet.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.CreatedOn).Select(s => s.Id).FirstOrDefaultAsync(ct);
            if (firstId == sessionId)
            {
                var adopt = await _db.WhatsAppOutboundMessageDbSet.Where(m => m.SessionId == null).ToListAsync(ct);
                if (adopt.Count > 0) { foreach (var a in adopt) a.SessionId = sessionId; await _db.SaveChangesAsync(ct); }   // from now on they are plain rows of this number
            }
            var mine = _db.WhatsAppOutboundMessageDbSet.Where(m => m.SessionId == sessionId);
            var today = DateTime.UtcNow.Date;
            var sentToday = await mine.CountAsync(m => m.Status == (int)OutboundStatus.Sent && m.SentOn >= today, ct);
            var lastSent = await mine.Where(m => m.Status == (int)OutboundStatus.Sent).MaxAsync(m => (DateTime?)m.SentOn, ct);

            var due = await mine
                .Where(m => m.Status == (int)OutboundStatus.Pending && m.ScheduledFor <= DateTime.UtcNow)
                .OrderBy(m => m.ScheduledFor).Take(20).ToListAsync(ct);

            // wave limit: at most MaxPerWindow messages inside the last WindowMinutes
            var sentInWindow = 0;
            if (setting.MaxPerWindow.HasValue && setting.WindowMinutes.HasValue)
            {
                var since = DateTime.UtcNow.AddMinutes(-setting.WindowMinutes.Value);
                sentInWindow = await mine.CountAsync(m => m.Status == (int)OutboundStatus.Sent && m.SentOn >= since, ct);
            }

            var allowedJids = await _db.WhatsAppGroupDbSet.AsNoTracking().Where(g => g.SessionId == sessionId && g.IsAllowed && g.IsActive).Select(g => g.GroupJid).ToListAsync(ct);

            foreach (var m in due)
            {
                // a row that reached the outbox another way (a SQL insert) is still checked against the allow-list: it is refused, not sent
                if (allowedJids.Count > 0 && !allowedJids.Contains(m.GroupJid))
                {
                    m.Status = (int)OutboundStatus.Failed; m.ErrorMessage = "Group is not on the allowed list of this number.";
                    await _db.SaveChangesAsync(ct);
                    continue;
                }
                if (setting.MaxMessagesPerDay.HasValue && sentToday >= setting.MaxMessagesPerDay.Value) break;          // daily cap reached
                if (setting.MaxPerWindow.HasValue && setting.WindowMinutes.HasValue && sentInWindow >= setting.MaxPerWindow.Value) break;   // this wave is full
                if (lastSent.HasValue && (DateTime.UtcNow - lastSent.Value).TotalSeconds < setting.MinSecondsBetweenMessages) break; // too soon, try next tick
                if (_nextAllowedUtc.TryGetValue(sessionId, out var next) && DateTime.UtcNow < next) break;               // random jitter gap not over yet

                m.Attempts++;
                SendResult result;
                try
                {
                    if (m.MediaId.HasValue)
                    {
                        var media = await _media.GetAsync(m.MediaId.Value);
                        result = media == null || !File.Exists(_media.FullPath(media))
                            ? new SendResult(false, "The file of this message is missing from the media storage.")
                            : await client.SendGroupMediaAsync(m.GroupJid, _media.FullPath(media), media.MimeType, m.Body, ct);
                    }
                    else result = await client.SendGroupMessageAsync(m.GroupJid, m.Body, ct);
                }
                catch (Exception ex) { result = new SendResult(false, ex.Message); }   // one failing message must not stop the loop (fail-open)

                if (result.Success) { m.Status = (int)OutboundStatus.Sent; m.SentOn = DateTime.UtcNow; m.ErrorMessage = null; sentToday++; sentInWindow++; sent++; lastSent = m.SentOn; _nextAllowedUtc[sessionId] = DateTime.UtcNow.AddSeconds(Random.Shared.Next(0, setting.JitterSeconds + 1)); }
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
        catch (Exception ex) { _logger.LogError(ex, "WhatsApp dispatch failed for session {SessionId}. Nothing else is affected.", sessionId); }
        return sent;
    }

    public async Task<List<WhatsAppOutboundMessage>> ListAsync(Guid sessionId, int take)
    {
        var firstId = await _db.WhatsAppSessionDbSet.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.CreatedOn).Select(s => s.Id).FirstOrDefaultAsync();
        var isFirst = firstId == sessionId;   // rows without a SessionId (inserted by SQL) show under the first number
        return await _db.WhatsAppOutboundMessageDbSet.AsNoTracking().Where(m => m.SessionId == sessionId || (isFirst && m.SessionId == null)).OrderByDescending(m => m.CreatedOn).Take(take).ToListAsync();
    }
}

public class WhatsAppInboundUseCase : IWhatsAppInboundUseCase
{
    private readonly IApplicationDbContext _db;
    private readonly IWhatsAppMediaUseCase _media;
    private readonly ILogger<WhatsAppInboundUseCase> _logger;
    public WhatsAppInboundUseCase(IApplicationDbContext db, IWhatsAppMediaUseCase media, ILogger<WhatsAppInboundUseCase> logger) { _db = db; _media = media; _logger = logger; }

    public async Task RecordAsync(Guid sessionId, InboundMessage message)
    {
        try
        {
            var setting = await _db.WhatsAppSettingDbSet.AsNoTracking().FirstOrDefaultAsync(s => s.SessionId == sessionId);
            if (setting == null || !setting.IsEnabled || !setting.ReceiveEnabled) return;

            // only groups we know about are stored (privacy: do not keep chats from groups nobody asked for)
            var known = await _db.WhatsAppGroupDbSet.AnyAsync(g => g.SessionId == sessionId && g.GroupJid == message.GroupJid && g.IsActive);
            if (!known) return;
            // allow-list: once any group of this number is allowed, messages of other groups are dropped, not stored
            var restricted = await _db.WhatsAppGroupDbSet.AnyAsync(g => g.SessionId == sessionId && g.IsAllowed && g.IsActive);
            if (restricted && !await _db.WhatsAppGroupDbSet.AnyAsync(g => g.SessionId == sessionId && g.GroupJid == message.GroupJid && g.IsAllowed && g.IsActive)) return;

            // a received file: move it from the client's temporary path into the media storage (type, size and hash are recorded there)
            Guid? mediaId = null; var type = "text";
            if (!string.IsNullOrEmpty(message.MediaPath) && File.Exists(message.MediaPath))
            {
                try
                {
                    using var fs = File.OpenRead(message.MediaPath);
                    var saved = await _media.SaveAsync(fs, Path.GetFileName(message.MediaPath), message.MediaMime, 1, sessionId);
                    mediaId = saved.Id; type = saved.Kind;
                }
                catch (MediaRejectedException ex) { _logger.LogWarning("A received file was not stored: {Reason}", ex.Message); }
                finally { try { File.Delete(message.MediaPath); } catch { } }
            }

            // "processing" is the part you own: read the text, decide, act. The lab only marks it processed.
            _db.WhatsAppInboundMessageDbSet.Add(new WhatsAppInboundMessage
            {
                Id = Guid.NewGuid(), SessionId = sessionId, GroupJid = message.GroupJid, SenderJid = message.SenderJid, SenderName = message.SenderName,
                Body = message.Body ?? "", MessageType = type, MediaId = mediaId, ReceivedOn = message.ReceivedOn,
                ProcessedOn = DateTime.UtcNow, ProcessStatus = (int)InboundProcessStatus.Processed
            });
            await _db.SaveChangesAsync();
        }
        catch (Exception ex) { _logger.LogError(ex, "Could not record the received WhatsApp message."); }
    }

    public Task<List<WhatsAppInboundMessage>> ListAsync(Guid sessionId, int take) => _db.WhatsAppInboundMessageDbSet.AsNoTracking().Where(m => m.SessionId == sessionId).OrderByDescending(m => m.ReceivedOn).Take(take).ToListAsync();
}
