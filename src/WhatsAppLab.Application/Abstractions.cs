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
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public record WhatsAppLinkState(bool IsLinked, string PhoneNumber, string QrCode);
public record WhatsAppGroupInfo(string Jid, string Name, int ParticipantCount);
public record SendResult(bool Success, string Error);
public record InboundMessage(string GroupJid, string SenderJid, string SenderName, string Body, DateTime ReceivedOn);

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
    Task ConnectAsync(CancellationToken ct);
    /// <summary>Closes the browser but keeps the login, so Connect brings it back without a new QR.</summary>
    Task DisconnectAsync(CancellationToken ct);
    /// <summary>Closes the browser AND deletes the saved login. The next Connect needs a new QR scan.</summary>
    Task ResetSessionAsync(CancellationToken ct);
}

public interface IWhatsAppSettingUseCase
{
    Task<WhatsAppSetting> GetAsync();
    Task<WhatsAppSetting> SaveAsync(WhatsAppSetting input);
}

public interface IWhatsAppGroupUseCase
{
    Task<int> SyncAsync(CancellationToken ct);
    Task<List<WhatsAppGroup>> ListAsync();
}

public interface IWhatsAppOutboxUseCase
{
    /// <summary>Puts a message in the outbox. Does not send. Returns the new row id.</summary>
    Task<Guid> EnqueueAsync(string groupJid, string body, DateTime? scheduledFor, OutboundSource source, string sourceReference);
    /// <summary>Sends what is due, respecting the settings. Never throws: one bad message must not stop the rest. Returns how many were sent.</summary>
    Task<int> DispatchDueAsync(CancellationToken ct);
    Task<List<WhatsAppOutboundMessage>> ListAsync(int take);
}

public interface IWhatsAppInboundUseCase
{
    Task RecordAsync(InboundMessage message);
    Task<List<WhatsAppInboundMessage>> ListAsync(int take);
}
