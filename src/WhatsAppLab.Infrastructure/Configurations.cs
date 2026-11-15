using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppLab.Domain;

namespace WhatsAppLab.Infrastructure;

// One configuration class per entity, like Persistence/Configurations in the ERP.

public class WhatsAppSessionConfiguration : IEntityTypeConfiguration<WhatsAppSession>
{
    public void Configure(EntityTypeBuilder<WhatsAppSession> b)
    {
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

public class WhatsAppOutboundMessageConfiguration : IEntityTypeConfiguration<WhatsAppOutboundMessage>
{
    public void Configure(EntityTypeBuilder<WhatsAppOutboundMessage> b)
    {
        b.Property(x => x.GroupJid).HasMaxLength(100).IsRequired();
        b.Property(x => x.Body).HasMaxLength(4000).IsRequired();
        b.Property(x => x.ErrorMessage).HasMaxLength(1000);
        b.Property(x => x.SourceReference).HasMaxLength(200);
        b.Property(x => x.CorrelationId).HasMaxLength(64);
        b.HasIndex(x => new { x.Status, x.ScheduledFor });   // the dispatcher always asks for "pending and due"
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
