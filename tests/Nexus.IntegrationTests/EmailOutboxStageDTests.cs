using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nexus.Application.Email;
using Nexus.Domain;
using Nexus.Infrastructure.Email;
using Nexus.Infrastructure.Persistence;

namespace Nexus.IntegrationTests;

/// <summary>
/// The outbox against real SQL Server. The unique index is the mechanism, not a nicety,
/// so an in-memory provider would test the wrong thing.
/// </summary>
[Collection(NexusCollection.Name)]
public sealed class EmailOutboxStageDTests(NexusApp app)
{
    // The fixture owns the schema; this class only needs it to exist.
    private readonly NexusApp _app = app;

    // Every test starts from an empty outbox: a dispatcher pass claims whatever is queued,
    // including rows an earlier class left behind.
    private static Task<EmailStageDHost> HostAsync(Action<Dictionary<string, string?>>? configure = null) =>
        new EmailStageDHost(configure).ResetAsync();

    /// <summary>
    /// The headline requirement. Tap retries a webhook on anything that is not a prompt
    /// 2xx, and the customer must not receive three copies of their receipt.
    /// </summary>
    [Fact]
    public async Task A_replayed_webhook_queues_exactly_one_confirmation()
    {
        using var host = await HostAsync();
        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host);

        for (var replay = 0; replay < 3; replay++)
        {
            await host.InScopeAsync(async sp =>
            {
                var db = sp.GetRequiredService<NexusDbContext>();
                await sp.GetRequiredService<IOrderEmailService>().QueueOrderConfirmationAsync(order.Id);
                await db.SaveChangesAsync();
            });
        }

        var rows = await RowsAsync(host, order.Id);
        Assert.Single(rows);
        Assert.Equal(EmailKind.OrderConfirmation, rows[0].Kind);
        Assert.Equal($"order-confirmation:{order.Id}", rows[0].IdempotencyKey);
    }

    /// <summary>
    /// The negative of the same property, and the one that catches a helpful
    /// <c>SaveChangesAsync</c> finding its way into <c>EnqueueAsync</c>.
    /// </summary>
    [Fact]
    public async Task A_rolled_back_payment_transaction_leaves_no_outbox_row()
    {
        using var host = await HostAsync();
        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host);

        await host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<NexusDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();

            await sp.GetRequiredService<IOrderEmailService>().QueueOrderConfirmationAsync(order.Id);
            await db.SaveChangesAsync();

            await transaction.RollbackAsync();
        });

        Assert.Empty(await RowsAsync(host, order.Id));
    }

    [Fact]
    public async Task Enqueue_writes_nothing_of_its_own_accord()
    {
        using var host = await HostAsync();
        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host);

        // No SaveChanges at all: the caller's transaction decides whether the row exists.
        await host.InScopeAsync(sp =>
            sp.GetRequiredService<IOrderEmailService>().QueueOrderConfirmationAsync(order.Id));

        Assert.Empty(await RowsAsync(host, order.Id));
    }

    /// <summary>
    /// The default state until an SMTP provider is chosen. The row is written and visibly
    /// Suppressed, because an operator must be able to see that the customer was not
    /// emailed — a missing row is indistinguishable from a bug.
    /// </summary>
    [Fact]
    public async Task With_the_transport_off_the_row_is_written_and_visibly_suppressed()
    {
        using var host = await HostAsync(s => s["Email:Transport"] = "None");
        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host);

        await QueueAsync(host, order.Id);

        var row = Assert.Single(await RowsAsync(host, order.Id));
        Assert.Equal(EmailStatus.Suppressed, row.Status);
        Assert.Contains("sending disabled", row.LastError ?? "", StringComparison.Ordinal);
        Assert.Null(row.NextAttemptAt);
    }

    [Fact]
    public async Task A_suppressed_recipient_is_recorded_rather_than_skipped()
    {
        using var host = await HostAsync();
        var address = $"blocked-{Guid.NewGuid():N}@example.com";
        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host, email: address);

        await host.InScopeAsync(sp => sp.GetRequiredService<EmailSuppressionList>()
            .SuppressAsync(address, SuppressionReason.ManualBlock, "5.1.1"));

        await QueueAsync(host, order.Id);

        var row = Assert.Single(await RowsAsync(host, order.Id));
        Assert.Equal(EmailStatus.Suppressed, row.Status);
        Assert.Contains("suppressed", row.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// B14. The caller is inside the transaction that captured money; an exception from
    /// here would roll back a paid order and the customer would have been charged for
    /// nothing.
    /// </summary>
    [Fact]
    public async Task A_broken_outbox_never_throws_into_the_payment_transaction()
    {
        using var host = await HostAsync();
        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host);

        await host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<NexusDbContext>();

            var service = new OrderEmailService(
                db,
                new ThrowingOutbox(),
                host.Settings,
                sp.GetRequiredService<IEmailSuppressionList>(),
                sp.GetRequiredService<IOptions<EmailOptions>>(),
                NullLogger<OrderEmailService>.Instance,
                host.Clock);

            // The assertion is that this returns at all.
            await service.QueueOrderConfirmationAsync(order.Id);
            await db.SaveChangesAsync();
        });

        await host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<NexusDbContext>();
            var reloaded = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
            Assert.Equal(OrderStatus.Paid, reloaded.Status);
        });

        // and the failure is visible rather than silent
        var row = Assert.Single(await RowsAsync(host, order.Id));
        Assert.Equal(EmailStatus.Failed, row.Status);
        Assert.Contains("enqueue threw", row.LastError ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unpaid_order_queues_nothing()
    {
        using var host = await HostAsync();
        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host, status: OrderStatus.Pending, withGrant: false);

        await QueueAsync(host, order.Id);

        Assert.Empty(await RowsAsync(host, order.Id));
    }

    [Fact]
    public async Task The_language_comes_from_the_order_not_the_customer_row()
    {
        using var host = await HostAsync();
        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host, language: "ar");

        await QueueAsync(host, order.Id);

        Assert.Equal("ar", Assert.Single(await RowsAsync(host, order.Id)).Language);
    }

    /// <summary>
    /// A replayed refund webhook collapses onto one notice; a genuine second partial
    /// refund has moved the running total on and gets its own.
    /// </summary>
    [Fact]
    public async Task A_replayed_refund_notice_collapses_but_a_second_refund_does_not()
    {
        using var host = await HostAsync();
        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host);

        for (var replay = 0; replay < 2; replay++) await QueueRefundAsync(host, order.Id, 2.000m);

        await host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<NexusDbContext>();
            var tracked = await db.Orders.FirstAsync(o => o.Id == order.Id);
            tracked.RefundedTotalKwd = 2.000m;
            await db.SaveChangesAsync();
        });

        await QueueRefundAsync(host, order.Id, 2.000m);

        var notices = (await RowsAsync(host, order.Id)).Where(r => r.Kind == EmailKind.RefundNotice).ToList();
        Assert.Equal(2, notices.Count);
    }

    private static async Task QueueAsync(EmailStageDHost host, int orderId) =>
        await host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<NexusDbContext>();
            await sp.GetRequiredService<IOrderEmailService>().QueueOrderConfirmationAsync(orderId);
            await db.SaveChangesAsync();
        });

    private static async Task QueueRefundAsync(EmailStageDHost host, int orderId, decimal amount) =>
        await host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<NexusDbContext>();
            await sp.GetRequiredService<IOrderEmailService>().QueueRefundNoticeAsync(orderId, amount);
            await db.SaveChangesAsync();
        });

    private static Task<List<Domain.Entities.OutboxEmail>> RowsAsync(EmailStageDHost host, int orderId) =>
        host.InScopeAsync(sp => sp.GetRequiredService<NexusDbContext>()
            .OutboxEmails.AsNoTracking()
            .Where(e => e.OrderId == orderId)
            .OrderBy(e => e.Id)
            .ToListAsync());
}
