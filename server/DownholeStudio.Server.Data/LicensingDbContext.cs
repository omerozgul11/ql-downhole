using System.Text;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DownholeStudio.Server.Data;

/// <summary>PostgreSQL database of the licensing service (snake_case tables and columns).</summary>
public sealed class LicensingDbContext(DbContextOptions<LicensingDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<LicenseProduct> LicenseProducts => Set<LicenseProduct>();
    public DbSet<License> Licenses => Set<License>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Activation> Activations => Set<Activation>();
    public DbSet<TrialDevice> TrialDevices => Set<TrialDevice>();
    public DbSet<LicenseEvent> LicenseEvents => Set<LicenseEvent>();
    public DbSet<SoftwareVersion> SoftwareVersions => Set<SoftwareVersion>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<EmailMessage> EmailOutbox => Set<EmailMessage>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();

    /// <summary>ASP.NET Core data-protection keys (admin portal cookies).</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Customer>(e =>
        {
            e.ToTable("customers");
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Company).HasMaxLength(200);
            e.Property(x => x.ExternalCustomerId).HasMaxLength(200);
            e.Ignore(x => x.DisplayName);
        });
        b.Entity<LicenseProduct>(e =>
        {
            e.ToTable("license_products");
            e.Property(x => x.Id).HasMaxLength(64);
            e.HasData(new LicenseProduct { Id = Licensing.ProductIds.DownholeStudio, Name = "Downhole Studio", DefaultEdition = "Professional", Active = true, TrialDays = 14 });
        });
        b.Entity<License>(e =>
        {
            e.ToTable("licenses");
            e.HasIndex(x => x.LicenseKeyHash).IsUnique();
            e.HasIndex(x => x.CustomerId);
            e.HasIndex(x => x.KeyHint);
            e.Property(x => x.LicenseKeyHash).HasMaxLength(64);
            e.Property(x => x.KeyHint).HasMaxLength(8);
            e.Property(x => x.Product).HasMaxLength(64);
            e.Property(x => x.Edition).HasMaxLength(64);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne(x => x.Customer).WithMany(c => c.Licenses).HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Device>(e =>
        {
            e.ToTable("devices");
            e.HasIndex(x => x.DeviceHash).IsUnique();
            e.HasIndex(x => x.HardwareHash);
            e.Property(x => x.DeviceHash).HasMaxLength(64);
            e.Property(x => x.HardwareHash).HasMaxLength(64);
            e.Property(x => x.MachineName).HasMaxLength(128);
            e.Ignore(x => x.Label);
        });
        b.Entity<Activation>(e =>
        {
            e.ToTable("activations");
            e.HasIndex(x => new { x.LicenseId, x.DeviceId });
            e.HasOne(x => x.License).WithMany(l => l.Activations).HasForeignKey(x => x.LicenseId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Device).WithMany(d => d.Activations).HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
            e.Ignore(x => x.IsActive);
        });
        b.Entity<TrialDevice>(e =>
        {
            e.ToTable("trial_devices");
            e.HasIndex(x => new { x.Product, x.DeviceHash }).IsUnique();
            e.HasIndex(x => new { x.Product, x.HardwareHash });
            e.Property(x => x.DeviceHash).HasMaxLength(64);
            e.Property(x => x.HardwareHash).HasMaxLength(64);
        });
        b.Entity<LicenseEvent>(e =>
        {
            e.ToTable("license_events");
            e.HasIndex(x => x.LicenseId);
            e.HasIndex(x => x.CreatedAt);
            e.Property(x => x.Details).HasColumnType("jsonb");
            e.Property(x => x.EventType).HasMaxLength(64);
        });
        b.Entity<SoftwareVersion>(e =>
        {
            e.ToTable("software_versions");
            e.HasIndex(x => new { x.Product, x.Version, x.Channel }).IsUnique();
            e.Property(x => x.Sha256).HasMaxLength(64);
        });
        b.Entity<Payment>(e =>
        {
            e.ToTable("payments");
            e.HasIndex(x => x.ProviderSessionId);
        });
        b.Entity<AuditLogEntry>(e =>
        {
            e.ToTable("audit_log");
            e.HasIndex(x => x.CreatedAt);
            e.Property(x => x.Before).HasColumnType("jsonb");
            e.Property(x => x.After).HasColumnType("jsonb");
        });
        b.Entity<AdminUser>(e =>
        {
            e.ToTable("admin_users");
            e.HasIndex(x => x.Email).IsUnique();
        });
        b.Entity<EmailMessage>(e =>
        {
            e.ToTable("email_outbox");
            e.HasIndex(x => new { x.SentAt, x.NextAttemptAt });
        });
        b.Entity<WebhookEvent>(e => e.ToTable("webhook_events"));
        b.Entity<DataProtectionKey>(e => e.ToTable("data_protection_keys"));

        ApplySnakeCase(b);
    }

    /// <summary>Renames every column, key, index and foreign key to snake_case.</summary>
    private static void ApplySnakeCase(ModelBuilder b)
    {
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var p in entity.GetProperties())
            {
                p.SetColumnName(ToSnake(p.Name));
            }

            foreach (var k in entity.GetKeys())
            {
                k.SetName(ToSnake(k.GetName() ?? ""));
            }

            foreach (var fk in entity.GetForeignKeys())
            {
                fk.SetConstraintName(ToSnake(fk.GetConstraintName() ?? ""));
            }

            foreach (var ix in entity.GetIndexes())
            {
                ix.SetDatabaseName(ToSnake(ix.GetDatabaseName() ?? ""));
            }
        }
    }

    /// <summary>"LicenseKeyHash" → "license_key_hash".</summary>
    public static string ToSnake(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && name[i - 1] != '_' && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}

/// <summary>Design-time factory for `dotnet ef` (no database connection is opened).</summary>
public sealed class LicensingDbContextFactory : IDesignTimeDbContextFactory<LicensingDbContext>
{
    public LicensingDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Licensing") ?? "Host=localhost;Database=dhs_licensing;Username=dhs;Password=design-time-only";
        return new LicensingDbContext(new DbContextOptionsBuilder<LicensingDbContext>().UseNpgsql(cs).Options);
    }
}
