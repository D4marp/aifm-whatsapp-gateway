using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppLab.Domain;

namespace WhatsAppLab.Infrastructure;

// One configuration class per entity, like Persistence/Configurations in the ERP.

public class WhatsAppSessionConfiguration : IEntityTypeConfiguration<WhatsAppSession>
{
    public void Configure(EntityTypeBuilder<WhatsAppSession> b)
    {
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.ProfileKey).HasMaxLength(100);
        b.HasIndex(x => x.ProfileKey);
        b.Property(x => x.PhoneNumber).HasMaxLength(30);
        b.Property(x => x.LibraryName).HasMaxLength(100);
    }
}

public class WhatsAppGroupConfiguration : IEntityTypeConfiguration<WhatsAppGroup>
{
    public void Configure(EntityTypeBuilder<WhatsAppGroup> b)
    {
        b.Property(x => x.GroupJid).HasMaxLength(100).IsRequired();
        b.Property(x => x.Name).HasMaxLength(200);
        b.HasIndex(x => new { x.SessionId, x.GroupJid });
    }
}

public class WhatsAppMediaConfiguration : IEntityTypeConfiguration<WhatsAppMedia>
{
    public void Configure(EntityTypeBuilder<WhatsAppMedia> b)
    {
        b.Property(x => x.Kind).HasMaxLength(20);
        b.Property(x => x.MimeType).HasMaxLength(100);
        b.Property(x => x.FileName).HasMaxLength(255);
        b.Property(x => x.Sha256).HasMaxLength(64);
        b.Property(x => x.StoragePath).HasMaxLength(500);
        b.HasIndex(x => x.Sha256);
    }
}

public class WhatsAppOutboundMessageConfiguration : IEntityTypeConfiguration<WhatsAppOutboundMessage>
{
    public void Configure(EntityTypeBuilder<WhatsAppOutboundMessage> b)
    {
        b.Property(x => x.GroupJid).HasMaxLength(100).IsRequired();
        b.Property(x => x.Body).HasMaxLength(4000).IsRequired();   // a file message keeps its caption here (an empty string when there is none)
        b.Property(x => x.MessageType).HasMaxLength(20);
        b.Property(x => x.ErrorMessage).HasMaxLength(1000);
        b.Property(x => x.SourceReference).HasMaxLength(200);
        b.Property(x => x.CorrelationId).HasMaxLength(64);
        b.HasIndex(x => new { x.SessionId, x.Status, x.ScheduledFor });   // the dispatcher asks, per number, for "pending and due"
    }
}

public class WhatsAppInboundMessageConfiguration : IEntityTypeConfiguration<WhatsAppInboundMessage>
{
    public void Configure(EntityTypeBuilder<WhatsAppInboundMessage> b)
    {
        b.Property(x => x.GroupJid).HasMaxLength(100).IsRequired();
        b.Property(x => x.SenderJid).HasMaxLength(100);
        b.Property(x => x.SenderName).HasMaxLength(200);
        b.Property(x => x.Body).HasMaxLength(4000);
        b.Property(x => x.MessageType).HasMaxLength(30);
        b.HasIndex(x => new { x.GroupJid, x.ReceivedOn });
    }
}

public class WhatsAppSettingConfiguration : IEntityTypeConfiguration<WhatsAppSetting>
{
    public void Configure(EntityTypeBuilder<WhatsAppSetting> b) { }
}
