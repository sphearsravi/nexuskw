using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexus.Domain.Entities;

namespace Nexus.Infrastructure.Persistence.Configurations;

public sealed class DownloadGrantConfiguration : IEntityTypeConfiguration<DownloadGrant>
{
    public void Configure(EntityTypeBuilder<DownloadGrant> e)
    {
        e.HasKey(x => x.Id);
        e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
        e.Property(x => x.TokenProtected).HasMaxLength(512);
        e.Property(x => x.State).HasConversion<int>();
        e.Property(x => x.RevocationReason).HasConversion<int>();

        e.HasOne(x => x.Order).WithMany()
         .HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne(x => x.Book).WithMany()
         .HasForeignKey(x => x.BookId).OnDelete(DeleteBehavior.Restrict);

        // The hash is the only thing ever looked up; the plaintext token never
        // appears in a query.
        e.HasIndex(x => x.TokenHash).IsUnique();
        e.HasIndex(x => new { x.OrderId, x.BookId, x.Sequence }).IsUnique();
        e.HasIndex(x => x.OrderId);
        e.HasIndex(x => new { x.State, x.ExpiresAt });
        e.HasIndex(x => x.FlaggedAt).HasFilter("[FlaggedAt] IS NOT NULL");
    }
}

public sealed class DownloadLogConfiguration : IEntityTypeConfiguration<DownloadLog>
{
    public void Configure(EntityTypeBuilder<DownloadLog> e)
    {
        e.HasKey(x => x.Id);
        e.Property(x => x.IpPrefix).HasMaxLength(45);
        e.Property(x => x.UserAgent).HasMaxLength(512);
        e.Property(x => x.Outcome).HasConversion<int>();

        e.HasOne(x => x.Grant).WithMany(g => g.Logs)
         .HasForeignKey(x => x.GrantId).OnDelete(DeleteBehavior.Cascade);

        e.HasIndex(x => new { x.GrantId, x.AtUtc }).IsDescending(false, true);
        e.HasIndex(x => x.AtUtc);
        e.HasIndex(x => new { x.GrantId, x.IpPrefix });
    }
}

public sealed class SettingConfiguration : IEntityTypeConfiguration<Setting>
{
    public void Configure(EntityTypeBuilder<Setting> e)
    {
        e.HasKey(x => x.Key);
        e.Property(x => x.Key).HasMaxLength(128);
        // nvarchar(max): the seeded English confirmation body is already ~700
        // characters and the operator will add a paragraph.
        e.Property(x => x.Value).IsRequired();
        e.Property(x => x.UpdatedBy).HasMaxLength(128);

        // No HasData. It rewrites seed rows on the next migration that touches them,
        // which would destroy an operator's edited email copy. The runtime seeder
        // leaves an existing key alone.
    }
}

public sealed class OutboxEmailConfiguration : IEntityTypeConfiguration<OutboxEmail>
{
    public void Configure(EntityTypeBuilder<OutboxEmail> e)
    {
        e.HasKey(x => x.Id);
        e.Property(x => x.Kind).HasConversion<int>();
        e.Property(x => x.ToEmail).HasMaxLength(256).IsRequired();
        e.Property(x => x.Language).HasMaxLength(2).IsRequired();
        e.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
        e.Property(x => x.PublicRef).HasMaxLength(64).IsRequired();
        e.Property(x => x.MessageId).HasMaxLength(256);
        e.Property(x => x.Subject).HasMaxLength(256);
        e.Property(x => x.Status).HasConversion<int>();
        e.Property(x => x.LastError).HasMaxLength(1024);

        e.HasOne(x => x.Order).WithMany()
         .HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne(x => x.Customer).WithMany()
         .HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.SetNull);

        e.HasIndex(x => x.IdempotencyKey).IsUnique();
        e.HasIndex(x => x.PublicRef).IsUnique();
        // Queued and Sending only — the dispatcher's hot path.
        e.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasFilter("[Status] IN (0,1)");
        e.HasIndex(x => new { x.OrderId, x.CreatedAt }).IsDescending(false, true);
        e.HasIndex(x => new { x.ToEmail, x.CreatedAt }).IsDescending(false, true);
    }
}

public sealed class EmailSuppressionConfiguration : IEntityTypeConfiguration<EmailSuppression>
{
    public void Configure(EntityTypeBuilder<EmailSuppression> e)
    {
        e.HasKey(x => x.Id);
        e.Property(x => x.NormalisedEmail).HasMaxLength(256).IsRequired();
        e.Property(x => x.Reason).HasConversion<int>();
        e.Property(x => x.LastClassification).HasMaxLength(32);

        e.HasIndex(x => x.NormalisedEmail).IsUnique();
    }
}

public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> e)
    {
        e.HasKey(x => x.Id);
        e.Property(x => x.ActorId).HasMaxLength(450);
        e.Property(x => x.ActorEmail).HasMaxLength(256);
        e.Property(x => x.Entity).HasMaxLength(64).IsRequired();
        e.Property(x => x.EntityId).HasMaxLength(64).IsRequired();
        e.Property(x => x.Action).HasMaxLength(64).IsRequired();
        e.Property(x => x.IpAddress).HasMaxLength(45);

        e.HasIndex(x => new { x.Entity, x.EntityId, x.At });
    }
}
