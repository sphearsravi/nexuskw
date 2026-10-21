using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Domain;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Persistence;

namespace Nexus.IntegrationTests;

/// <summary>
/// The v1 money invariants are guarded by the database, not by good intentions. These
/// tests prove the guards actually fire — a CHECK constraint nobody has seen reject
/// anything is decoration.
/// </summary>
[Collection(NexusCollection.Name)]
public sealed class SchemaGuardTests(NexusApp app)
{
    private NexusDbContext Db(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<NexusDbContext>();

    private static Customer NewCustomer(string tag) => new()
    {
        Email = $"{tag}@example.com",
        NormalisedEmail = Customer.Normalise($"{tag}@example.com"),
        Name = "Test Buyer",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static Order NewOrder(int customerId, string number) => new()
    {
        OrderNumber = number,
        CustomerId = customerId,
        BuyerNameSnapshot = "Test Buyer",
        BuyerEmailSnapshot = "buyer@example.com",
        Language = "en",
        Currency = "KWD",
        SubtotalKwd = 6.500m,
        VatRate = 0m,
        VatAmount = 0m,
        TotalKwd = 6.500m,
        FxRate = 1m,
        TotalCharged = 6.500m,
        PaymentExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private async Task<Exception?> TryInsert(string tag, Action<Order> mutate)
    {
        using var scope = app.Services.CreateScope();
        var db = Db(scope);
        var customer = NewCustomer(tag);
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        var order = NewOrder(customer.Id, $"KW-260101-{tag.ToUpperInvariant()[..6]}");
        mutate(order);
        db.Orders.Add(order);
        try { await db.SaveChangesAsync(); return null; }
        catch (DbUpdateException ex) { return ex; }
    }

    [Fact]
    public async Task A_well_formed_kwd_order_is_accepted() =>
        Assert.Null(await TryInsert("ok0001", _ => { }));

    // v1 is Kuwait only. A UAE launch has to drop this constraint deliberately,
    // rather than a column quietly changing value one day.
    [Fact]
    public async Task A_non_kwd_currency_is_rejected() =>
        Assert.NotNull(await TryInsert("cur001", o => o.Currency = "AED"));

    [Fact]
    public async Task A_non_unit_fx_rate_is_rejected() =>
        Assert.NotNull(await TryInsert("fxx001", o => o.FxRate = 12.05m));

    [Fact]
    public async Task Any_vat_is_rejected() =>
        Assert.NotNull(await TryInsert("vat001", o => { o.VatRate = 0.05m; o.VatAmount = 0.325m; }));

    [Fact]
    public async Task A_total_that_does_not_equal_subtotal_plus_vat_is_rejected() =>
        Assert.NotNull(await TryInsert("tot001", o => o.TotalKwd = 9.999m));

    [Fact]
    public async Task A_zero_total_is_rejected() =>
        Assert.NotNull(await TryInsert("zer001", o =>
        {
            o.SubtotalKwd = 0m; o.TotalKwd = 0m; o.TotalCharged = 0m;
        }));

    [Fact]
    public async Task A_refund_larger_than_the_amount_charged_is_rejected() =>
        Assert.NotNull(await TryInsert("ref001", o => o.RefundedTotalKwd = 99.000m));

    /// <summary>
    /// The whole reason KWD is handled as decimal(18,3): 6.500 must come back as 6.500,
    /// not 6.50. A truncation here makes every Tap signature fail, silently.
    /// </summary>
    [Fact]
    public async Task Three_decimal_places_survive_a_round_trip()
    {
        using var scope = app.Services.CreateScope();
        var db = Db(scope);
        var customer = NewCustomer("dec001");
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        var order = NewOrder(customer.Id, "KW-260101-DEC001");
        order.SubtotalKwd = order.TotalKwd = order.TotalCharged = 18.750m;
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var read = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
        Assert.Equal(18.750m, read.TotalCharged);
        Assert.Equal("18.750", Currency.Kwd.Format(read.TotalCharged));
    }

    /// <summary>
    /// Blocker fix: the webhook idempotency index is filtered on HashValid. A forged,
    /// unverified delivery must not be able to occupy the slot and cause the genuine
    /// notification to be swallowed as a duplicate.
    /// </summary>
    [Fact]
    public async Task A_rejected_webhook_cannot_occupy_the_idempotency_slot()
    {
        using var scope = app.Services.CreateScope();
        var db = Db(scope);

        db.WebhookEvents.Add(new WebhookEvent
        {
            TapObjectId = "chg_slot_test", Status = "CAPTURED",
            HashValid = false, Outcome = WebhookOutcome.RejectedSignature,
            ReceivedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        // The genuine one lands afterwards and must be accepted.
        db.WebhookEvents.Add(new WebhookEvent
        {
            TapObjectId = "chg_slot_test", Status = "CAPTURED",
            HashValid = true, Outcome = WebhookOutcome.Accepted,
            ReceivedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        // A second *verified* one with the same id and status is the real duplicate.
        db.WebhookEvents.Add(new WebhookEvent
        {
            TapObjectId = "chg_slot_test", Status = "CAPTURED",
            HashValid = true, Outcome = WebhookOutcome.Accepted,
            ReceivedAt = DateTimeOffset.UtcNow
        });
        var duplicate = await Record.ExceptionAsync(() => db.SaveChangesAsync());

        Assert.NotNull(duplicate);
        Assert.IsType<SqlException>(duplicate!.InnerException);
    }
}
