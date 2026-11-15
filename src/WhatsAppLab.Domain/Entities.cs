namespace WhatsAppLab.Domain;

// Mirrors the ERP's BinaryPlate.Domain.Common.Models.Entity: Id, soft delete and audit columns.
public abstract class Entity
{
    public Guid Id { get; set; }
    public bool IsDeleted { get; set; }
    public string CreatedBy { get; set; }
    public DateTime CreatedOn { get; set; }
    public string ModifiedBy { get; set; }
    public DateTime? ModifiedOn { get; set; }
    public string DeletedBy { get; set; }
    public DateTime? DeletedOn { get; set; }
}

public enum WhatsAppSessionStatus { NotLinked = 0, Linked = 1, Disconnected = 2 }

/// <summary>0 Pending, 1 Sent, 2 Failed. The old ERP table AttendanceNotification uses -1 / 0 / 1 instead.</summary>
public enum OutboundStatus { Pending = 0, Sent = 1, Failed = 2 }

/// <summary>Where a message came from. Shows the three triggers of the ticket: API, SQL Server, schedule.</summary>
public enum OutboundSource { Api = 0, SqlTrigger = 1, Scheduled = 2 }

public enum InboundProcessStatus { New = 0, Processed = 1, Ignored = 2 }

/// <summary>The linked WhatsApp number and its session. SessionData must be encrypted in a real system (the key is outside the database).</summary>
public class WhatsAppSession : Entity
{
    public string PhoneNumber { get; set; }
    public int Status { get; set; }
    public byte[] SessionData { get; set; }
    public DateTime? LinkedOn { get; set; }
    public DateTime? LastSeenOn { get; set; }
    public string LibraryName { get; set; }
}

/// <summary>A group the number has joined. Maps the group NAME (what NotificationSetupForWork keeps) to the group ID (what a library sends to).</summary>
public class WhatsAppGroup : Entity
{
    public Guid SessionId { get; set; }
    public string GroupJid { get; set; }
    public string Name { get; set; }
    public int ParticipantCount { get; set; }
    public DateTime LastSyncedOn { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>The outbox: one row is one message. API, SQL Server and schedule all write here; the dispatcher sends.</summary>
public class WhatsAppOutboundMessage : Entity
{
    public string GroupJid { get; set; }
    public string Body { get; set; }
    public DateTime ScheduledFor { get; set; }
    public int Status { get; set; }
    public int Attempts { get; set; }
    public DateTime? SentOn { get; set; }
    public string ErrorMessage { get; set; }
    public int Source { get; set; }
    public string SourceReference { get; set; }
    public string CorrelationId { get; set; }
}

/// <summary>A group message that was received. Holds personal data (sender, text), so keep only what is needed.</summary>
public class WhatsAppInboundMessage : Entity
{
    public string GroupJid { get; set; }
    public string SenderJid { get; set; }
    public string SenderName { get; set; }
    public string Body { get; set; }
    public string MessageType { get; set; }
    public DateTime ReceivedOn { get; set; }
    public DateTime? ProcessedOn { get; set; }
    public int ProcessStatus { get; set; }
}

/// <summary>One row of settings. No row = feature Off (same idea as FaceVerificationSettingDbSet in the ERP).</summary>
public class WhatsAppSetting : Entity
{
    public bool IsEnabled { get; set; }
    public int? MaxMessagesPerDay { get; set; }
    public int MinSecondsBetweenMessages { get; set; } = 5;
    public bool ReceiveEnabled { get; set; }
    public int? InboundRetentionDays { get; set; }

    // Pacing, so the traffic looks like a person and not a machine (advice from the team: about 10-15 a day, in uneven waves).
    /// <summary>Send only from this hour (0-23, server local time). Null = any hour.</summary>
    public int? SendFromHour { get; set; }
    /// <summary>... until this hour (exclusive). Null = any hour.</summary>
    public int? SendToHour { get; set; }
    /// <summary>At most this many messages inside WindowMinutes. Null = no window limit.</summary>
    public int? MaxPerWindow { get; set; }
    public int? WindowMinutes { get; set; }
    /// <summary>A random 0..JitterSeconds is added to the minimum gap between two messages.</summary>
    public int JitterSeconds { get; set; }
}
