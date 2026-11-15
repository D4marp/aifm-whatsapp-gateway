using Microsoft.EntityFrameworkCore;
using WhatsAppLab.Application;
using WhatsAppLab.Domain;

namespace WhatsAppLab.Infrastructure;

// Same conventions as the ERP's ApplicationDbContext: DbSet property names end with "DbSet" (and become the table names),
// audit columns are filled in SaveChanges, and the mapping lives in configuration classes.
public class LabDbContext : DbContext, IApplicationDbContext
{
    public LabDbContext(DbContextOptions<LabDbContext> options) : base(options) { }

    public DbSet<WhatsAppSession> WhatsAppSessionDbSet { get; set; }
    public DbSet<WhatsAppGroup> WhatsAppGroupDbSet { get; set; }
    public DbSet<WhatsAppOutboundMessage> WhatsAppOutboundMessageDbSet { get; set; }
    public DbSet<WhatsAppInboundMessage> WhatsAppInboundMessageDbSet { get; set; }
    public DbSet<WhatsAppSetting> WhatsAppSettingDbSet { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LabDbContext).Assembly);

        // the ERP keeps the audit columns at nvarchar(50)
        foreach (var t in modelBuilder.Model.GetEntityTypes().Where(t => typeof(Entity).IsAssignableFrom(t.ClrType)))
            foreach (var col in new[] { nameof(Entity.CreatedBy), nameof(Entity.ModifiedBy), nameof(Entity.DeletedBy) })
                modelBuilder.Entity(t.ClrType).Property(col).HasMaxLength(50);

        base.OnModelCreating(modelBuilder);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        foreach (var e in ChangeTracker.Entries<Entity>())
        {
            if (e.State == EntityState.Added) { e.Entity.CreatedOn = now; e.Entity.CreatedBy ??= "system"; }
            else if (e.State == EntityState.Modified) { e.Entity.ModifiedOn = now; e.Entity.ModifiedBy ??= "system"; }
        }
        return base.SaveChangesAsync(cancellationToken);
    }
}
