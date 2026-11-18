using Microsoft.EntityFrameworkCore;
using WhatsAppLab.Domain;

namespace WhatsAppLab.Application;

// Same idea as the ERP's IApplicationDbContext: use cases talk to this, not to EF directly.
public interface IApplicationDbContext
{
    DbSet<WhatsAppSession> WhatsAppSessionDbSet { get; set; }
    DbSet<WhatsAppGroup> WhatsAppGroupDbSet { get; set; }
    DbSet<WhatsAppOutboundMessage> WhatsAppOutboundMessageDbSet { get; set; }
    DbSet<WhatsAppInboundMessage> WhatsAppInboundMessageDbSet { get; set; }
    DbSet<WhatsAppSetting> WhatsAppSettingDbSet { get; set; }
    DbSet<WhatsAppMedia> WhatsAppMediaDbSet { get; set; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public record WhatsAppLinkState(bool IsLinked, string PhoneNumber, string QrCode);
public record WhatsAppGroupInfo(string Jid, string Name, int ParticipantCount);
public record SendResult(bool Success, string Error);
/// <summary>MediaPath/MediaMime are set when the message is a file the client already saved to a temporary path; the inbound use case moves it into the media storage.</summary>
public record InboundMessage(string GroupJid, string SenderJid, string SenderName, string Body, DateTime ReceivedOn, string MediaPath = null, string MediaMime = null);

/// <summary>
/// THE ADAPTER. Everything WhatsApp-specific hides behind this interface.
/// The lab ships FakeWhatsAppClient (no WhatsApp involved). On Monday, the real library goes into RealWhatsAppClient and nothing else changes.
/// </summary>
public interface IWhatsAppClient
{
    string LibraryName { get; }
    Task<WhatsAppLinkState> GetLinkStateAsync(CancellationToken ct);
    Task<IReadOnlyList<WhatsAppGroupInfo>> ListGroupsAsync(CancellationToken ct);
    Task<SendResult> SendGroupMessageAsync(string groupJid, string body, CancellationToken ct);
    /// <summary>Sends a file (audio, video, image or other) with an optional caption. filePath is a local path the client can read.</summary>
    Task<SendResult> SendGroupMediaAsync(string groupJid, string filePath, string mimeType, string caption, CancellationToken ct);
    /// <summary>Raised when a group message arrives. The dispatcher hooks this up to IWhatsAppInboundUseCase.</summary>
    event Func<InboundMessage, Task> MessageReceived;
}

/// <summary>
/// Optional: a client that owns a browser session can be connected, disconnected and reset from the admin page (so the login is done from the lab, not from a console).
/// Connection is one of: disconnected, loading, qr, linked.
/// </summary>
public interface IWhatsAppSessionControl
{
    string Connection { get; }
    /// <summary>Tells the client whether it may read incoming group messages (the Receive setting). Reading opens chats, which marks them as read on the phone.</summary>
    void SetReceiving(bool on);
    /// <summary>The group names receiving may open. Empty = no restriction. Keeps unrelated chats from being opened (and marked read) and stored.</summary>
    void SetAllowedGroups(IReadOnlyCollection<string> names);
    Task ConnectAsync(CancellationToken ct);
    /// <summary>Closes the browser but keeps the login, so Connect brings it back without a new QR.</summary>
    Task DisconnectAsync(CancellationToken ct);
    /// <summary>Closes the browser AND deletes the saved login. The next Connect needs a new QR scan.</summary>
    Task ResetSessionAsync(CancellationToken ct);
}

/// <summary>
/// One WhatsApp client per number (session). The registry creates a client the first time a session is asked for and keeps it.
/// Several numbers = several clients, each with its own login, so one number never touches another.
/// </summary>
public interface IWhatsAppClientRegistry
{
    /// <summary>The client of this session (created on first use).</summary>
    IWhatsAppClient Get(WhatsAppSession session);
    /// <summary>Raised once for every client the registry creates, so the dispatcher can listen to its incoming messages.</summary>
    event Action<Guid, IWhatsAppClient> ClientCreated;
}

public interface IWhatsAppSessionUseCase
{
    /// <summary>Creates the first session ("Default", folder edge-profile) when there is none, and moves old rows (from before several numbers existed) to it. Safe to call on every start.</summary>
    Task EnsureDefaultAsync();
    Task<List<WhatsAppSession>> ListAsync();
    /// <summary>The session with this id, or the first (oldest) one when the id is null. Null when none exists.</summary>
    Task<WhatsAppSession> ResolveAsync(Guid? sessionId);
    Task<WhatsAppSession> CreateAsync(string name);
}

/// <summary>Stores and finds files (audio, video, images). Bytes go to a folder outside the database and outside the repo; the database keeps type, size, hash and path.</summary>
public interface IWhatsAppMediaUseCase
{
    /// <summary>Checks type and size, stores the file and returns its row. Throws MediaRejectedException with a readable reason when the file is not allowed.</summary>
    Task<WhatsAppMedia> SaveAsync(Stream content, string fileName, string mimeType, int direction, Guid? sessionId);
    Task<WhatsAppMedia> GetAsync(Guid id);
    /// <summary>The full path of the stored file (for a client that must read it).</summary>
    string FullPath(WhatsAppMedia media);
}

public class MediaRejectedException : Exception { public MediaRejectedException(string message) : base(message) { } }

// Every use case below works on ONE session (number). Pass the session you got from ResolveAsync.
public interface IWhatsAppSettingUseCase
{
    Task<WhatsAppSetting> GetAsync(Guid sessionId);
    Task<WhatsAppSetting> SaveAsync(Guid sessionId, WhatsAppSetting input);
}

public interface IWhatsAppGroupUseCase
{
    Task<int> SyncAsync(Guid sessionId, CancellationToken ct);
    Task<List<WhatsAppGroup>> ListAsync(Guid sessionId);
    /// <summary>Makes exactly these groups the allowed ones for this number (an empty list removes the restriction). Returns how many are allowed.</summary>
    Task<int> SetAllowedAsync(Guid sessionId, IEnumerable<string> groupJids);
    /// <summary>The names of the allowed groups, or an empty list when nothing is restricted.</summary>
    Task<List<string>> AllowedNamesAsync(Guid sessionId);
}

public interface IWhatsAppOutboxUseCase
{
    /// <summary>Puts a message in the outbox of this number. Does not send. Returns the new row id.</summary>
    Task<Guid> EnqueueAsync(Guid sessionId, string groupJid, string body, DateTime? scheduledFor, OutboundSource source, string sourceReference, Guid? mediaId = null);
    /// <summary>Sends what is due for this number, respecting its settings. Never throws: one bad message must not stop the rest. Returns how many were sent.</summary>
    Task<int> DispatchDueAsync(Guid sessionId, CancellationToken ct);
    Task<List<WhatsAppOutboundMessage>> ListAsync(Guid sessionId, int take);
}

public interface IWhatsAppInboundUseCase
{
    Task RecordAsync(Guid sessionId, InboundMessage message);
    Task<List<WhatsAppInboundMessage>> ListAsync(Guid sessionId, int take);
}
