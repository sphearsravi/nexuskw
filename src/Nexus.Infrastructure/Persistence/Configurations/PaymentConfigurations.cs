using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexus.Domain.Entities;

namespace Nexus.Infrastructure.Persistence.Configurations;

public sealed class PaymentAttemptConfiguration : IEntityTypeConfiguration<PaymentAttempt>
{
    public void Configure(EntityTypeBuilder<PaymentAttempt> e)
    {
        e.HasKey(x => x.Id);
        e.Property(x => x.IdempotentReference).HasMaxLength(24).IsRequired();
        e.Property(x => x.Status).HasConversion<int>();
        e.Property(x => x.TapChargeId).HasMaxLength(64);
        e.Property(x => x.TapPaymentReference).HasMaxLength(64);
        e.Property(x => x.TapGatewayReference).HasMaxLength(64);
        e.Property(x => x.PaymentMethod).HasMaxLength(32);
        e.Property(x => x.FailureCode).HasMaxLength(32);
        e.Property(x => x.FailureMessage).HasMaxLength(512);
        e.Property(x => x.AmountCharged).HasColumnType("decimal(18,3)");
        e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        e.Property(x => x.TransactionUrl).HasMaxLength(1024);

        e.HasOne(x => x.Order).WithMany()
         .HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);

        e.HasIndex(x => x.TapChargeId).IsUnique().HasFilter("[TapChargeId] IS NOT NULL");
        e.HasIndex(x => new { x.OrderId, x.AttemptNumber }).IsUnique();
        e.HasIndex(x => x.IdempotentReference).IsUnique();
        e.HasIndex(x => new { x.Status, x.CreatedAt });
        e.HasIndex(x => x.OrderId);
    }
}

public sealed class WebhookEventConfiguration : IEntityTypeConfiguration<WebhookEvent>
{
    public void Configure(EntityTypeBuilder<WebhookEvent> e)
    {
        e.HasKey(x => x.Id);
        e.Property(x => x.TapObjectId).HasMaxLength(64).IsRequired();
        e.Property(x => x.ObjectType).HasConversion<int>();
        e.Property(x => x.Status).HasMaxLength(32).IsRequired();
        e.Property(x => x.Outcome).HasConversion<int>();
        e.Property(x => x.RejectionReason).HasMaxLength(256);
        e.Property(x => x.RawBodySha256).HasMaxLength(64);
        e.Property(x => x.RemoteIp).HasMaxLength(64);

        e.HasOne(x => x.Order).WithMany()
         .HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.SetNull);

        // THE most important line in the schema.
        //
        // The repository is public, so the webhook URL and the body shape are known.
        // An unfiltered unique index lets anyone POST a forged body carrying a real
        // charge id, take the idempotency slot, and the genuine notification that
        // follows is swallowed as a duplicate: a paid customer with no book, caused by
        // an anonymous request. Filtering on HashValid means rejected deliveries are
        // still recorded in full but can never pre-empt a real one.
        e.HasIndex(x => new { x.TapObjectId, x.Status })
         .IsUnique()
         .HasFilter("[HashValid] = 1");
    }
}

public sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> e)
    {
        e.HasKey(x => x.Id);
        e.Property(x => x.TapRefundId).HasMaxLength(64);
        e.Property(x => x.AmountKwd).HasColumnType("decimal(18,3)");
        e.Property(x => x.Status).HasConversion<int>();
        e.Property(x => x.Origin).HasConversion<int>();
        e.Property(x => x.Reason).HasMaxLength(256);
        e.Property(x => x.RequestedByAdminUserId).HasMaxLength(450);

        e.HasOne(x => x.Order).WithMany()
         .HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne(x => x.PaymentAttempt).WithMany()
         .HasForeignKey(x => x.PaymentAttemptId).OnDelete(DeleteBehavior.Restrict);

        e.HasIndex(x => x.TapRefundId).IsUnique().HasFilter("[TapRefundId] IS NOT NULL");
    }
}

public sealed class OrderEventConfiguration : IEntityTypeConfiguration<OrderEvent>
{
    public void Configure(EntityTypeBuilder<OrderEvent> e)
    {
        e.HasKey(x => x.Id);
        e.Property(x => x.FromStatus).HasConversion<int>();
        e.Property(x => x.ToStatus).HasConversion<int>();
        e.Property(x => x.Source).HasConversion<int>();
        e.Property(x => x.Detail).HasMaxLength(512);

        e.HasOne(x => x.Order).WithMany()
         .HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne(x => x.WebhookEvent).WithMany()
         .HasForeignKey(x => x.WebhookEventId).OnDelete(DeleteBehavior.SetNull);
        e.HasOne(x => x.PaymentAttempt).WithMany()
         .HasForeignKey(x => x.PaymentAttemptId).OnDelete(DeleteBehavior.SetNull);

        e.HasIndex(x => new { x.OrderId, x.At });
    }
}

public sealed class JobLeaseConfiguration : IEntityTypeConfiguration<JobLease>
{
    public void Configure(EntityTypeBuilder<JobLease> e)
    {
        e.HasKey(x => x.Name);
        e.Property(x => x.Name).HasMaxLength(64);
        e.Property(x => x.HolderId).HasMaxLength(64).IsRequired();
    }
}
