using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexus.Domain.Entities;

namespace Nexus.Infrastructure.Persistence.Configurations;

public sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> e)
    {
        e.HasKey(x => x.Id);
        e.Property(x => x.Email).HasMaxLength(256).IsRequired();
        e.Property(x => x.NormalisedEmail).HasMaxLength(256).IsRequired();
        e.Property(x => x.Name).HasMaxLength(128).IsRequired();
        e.Property(x => x.Country).HasMaxLength(2).IsRequired();
        e.Property(x => x.PreferredLanguage).HasMaxLength(2).IsRequired();
        e.Property(x => x.IdentityUserId).HasMaxLength(450);

        e.HasIndex(x => x.NormalisedEmail).IsUnique();
        e.HasIndex(x => x.IdentityUserId);
    }
}

public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> e)
    {
        e.HasKey(x => x.Id);

        e.Property(x => x.OrderNumber).HasMaxLength(20).IsRequired();
        e.Property(x => x.BuyerNameSnapshot).HasMaxLength(128).IsRequired();
        e.Property(x => x.BuyerEmailSnapshot).HasMaxLength(256).IsRequired();
        e.Property(x => x.Status).HasConversion<int>();
        e.Property(x => x.StatusDetail).HasMaxLength(200);
        e.Property(x => x.ReviewReason).HasMaxLength(200);
        e.Property(x => x.Language).HasMaxLength(2).IsRequired();
        e.Property(x => x.Currency).HasMaxLength(3).IsRequired();

        // Every money column carries its scale explicitly. EF's default is two
        // decimals; one miss stores 6.500 as 6.50 and every Tap signature fails.
        e.Property(x => x.SubtotalKwd).HasColumnType("decimal(18,3)");
        e.Property(x => x.VatRate).HasColumnType("decimal(5,4)");
        e.Property(x => x.VatAmount).HasColumnType("decimal(18,3)");
        e.Property(x => x.TotalKwd).HasColumnType("decimal(18,3)");
        e.Property(x => x.FxRate).HasColumnType("decimal(18,6)");
        e.Property(x => x.TotalCharged).HasColumnType("decimal(18,3)");
        e.Property(x => x.RefundedTotalKwd).HasColumnType("decimal(18,3)").HasDefaultValue(0m);

        e.Property(x => x.TapChargeId).HasMaxLength(64);
        e.Property(x => x.TapPaymentReference).HasMaxLength(64);
        e.Property(x => x.TapGatewayReference).HasMaxLength(64);
        e.Property(x => x.PaymentMethod).HasMaxLength(32);
        e.Property(x => x.LastPaymentFailureCode).HasMaxLength(32);
        e.Property(x => x.IdempotencyKey).HasMaxLength(64);
        e.Property(x => x.RowVersion).IsRowVersion();

        e.HasOne(x => x.Customer)
         .WithMany(c => c.Orders)
         .HasForeignKey(x => x.CustomerId)
         .OnDelete(DeleteBehavior.Restrict);

        e.HasIndex(x => x.OrderNumber).IsUnique();
        e.HasIndex(x => x.TapChargeId).IsUnique().HasFilter("[TapChargeId] IS NOT NULL");
        e.HasIndex(x => x.IdempotencyKey).IsUnique().HasFilter("[IdempotencyKey] IS NOT NULL");
        e.HasIndex(x => new { x.CustomerId, x.CreatedAt }).IsDescending(false, true);
        e.HasIndex(x => new { x.Status, x.CreatedAt });
        e.HasIndex(x => new { x.Status, x.PaymentExpiresAt });

        // The v1 invariants, guarded by the database rather than by good intentions.
        // They fail at the moment of the accident instead of two phases later, and a
        // UAE launch has to drop them deliberately rather than a column quietly
        // changing value.
        e.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Order_Currency_V1", "[Currency] = 'KWD'");
            t.HasCheckConstraint("CK_Order_Fx_V1", "[FxRate] = 1");
            t.HasCheckConstraint("CK_Order_Vat_V1", "[VatRate] = 0 AND [VatAmount] = 0");
            t.HasCheckConstraint("CK_Order_TotalIdentity", "[TotalKwd] = [SubtotalKwd] + [VatAmount]");
            t.HasCheckConstraint("CK_Order_Charged", "[TotalCharged] = [TotalKwd]");
            t.HasCheckConstraint("CK_Order_TotalPositive", "[TotalKwd] > 0");
            t.HasCheckConstraint("CK_Order_RefundBound",
                "[RefundedTotalKwd] >= 0 AND [RefundedTotalKwd] <= [TotalCharged]");
        });
    }
}

public sealed class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> e)
    {
        e.HasKey(x => x.Id);
        e.Property(x => x.UnitPriceKwd).HasColumnType("decimal(18,3)");
        e.Property(x => x.TitleEnSnapshot).HasMaxLength(256).IsRequired();
        e.Property(x => x.TitleArSnapshot).HasMaxLength(256).IsRequired();
        e.Property(x => x.AuthorEnSnapshot).HasMaxLength(128).IsRequired();
        e.Property(x => x.AuthorArSnapshot).HasMaxLength(128).IsRequired();

        e.HasOne(x => x.Order).WithMany(o => o.Items)
         .HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);

        e.HasOne(x => x.Book).WithMany()
         .HasForeignKey(x => x.BookId).OnDelete(DeleteBehavior.Restrict);

        // A book is a file, not a countable thing (ST-5).
        e.HasIndex(x => new { x.OrderId, x.BookId }).IsUnique();

        e.ToTable(t => t.HasCheckConstraint("CK_OrderItem_PricePositive", "[UnitPriceKwd] > 0"));
    }
}
