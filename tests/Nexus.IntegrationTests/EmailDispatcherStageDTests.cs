using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Nexus.Application.Email;
using Nexus.Domain;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Email;
using Nexus.Infrastructure.Persistence;

namespace Nexus.IntegrationTests;

/// <summary>
/// The dispatcher against real SQL Server: the claim statement, the lease, the retry
/// schedule and the suppression rules.
/// </summary>
[Collection(NexusCollection.Name)]
public sealed class EmailDispatcherStageDTests(NexusApp app)
{
    private readonly NexusApp _app = app;

    // Every test starts from an empty outbox: a dispatcher pass claims whatever is queued,
    // including rows an earlier class left behind.
    private static Task<EmailStageDHost> HostAsync(Action<Dictionary<string, string?>>? configure = null) =>
        new EmailStageDHost(configure).ResetAsync();

    [Fact]
    public async Task A_queued_row_is_claimed_sent_and_marked_sent()
    {
        using var host = await HostAsync();
        var (order, _, token) = await EmailStageDData.PaidOrderAsync(host);
        await QueueAsync(host, order.Id);

        var processed = await PumpAsync(host);

        Assert.Equal(1, processed);
        var row = await RowAsync(host, order.Id);
        Assert.Equal(EmailStatus.Sent, row.Status);
        Assert.NotNull(row.SentAt);
        Assert.Null(row.NextAttemptAt);
        Assert.False(string.IsNullOrWhiteSpace(row.Subject));

        var message = Assert.Single(host.Sender.Sent);
        Assert.Equal(order.BuyerEmailSnapshot, message.ToEmail);
        Assert.Equal(order.BuyerNameSnapshot, message.ToName);
        Assert.Contains($"https://nexus.example/download.html?t={token}", message.Text, StringComparison.Ordinal);
        Assert.Equal($"bounces+{row.PublicRef}@nexus.example", message.ReturnPath);
        Assert.Equal($"{row.PublicRef}@nexus.example", message.MessageId);
    }

    /// <summary>
    /// The buyer's name as the order snapshotted it, never the live Customer row — which
    /// an unauthenticated guest checkout can rewrite.
    /// </summary>
    [Fact]
    public async Task The_recipient_name_is_the_order_snapshot()
    {
        using var host = await HostAsync();
        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host);
        await QueueAsync(host, order.Id);

        await host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<NexusDbContext>();
            var customer = await db.Customers.FirstAsync(c => c.Id == order.CustomerId);
            customer.Name = "Somebody Else";
            await db.SaveChangesAsync();
        });

        await PumpAsync(host);

        Assert.Equal(order.BuyerNameSnapshot, Assert.Single(host.Sender.Sent).ToName);
    }

    [Fact]
    public async Task A_transient_failure_reschedules_inside_the_first_window()
    {
        using var host = await HostAsync();
        host.Sender.Respond = _ => new EmailSendResult(
            EmailSendOutcome.TransientFailure, "451 4.3.0 try later", "4.3.0");

        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host);
        await QueueAsync(host, order.Id);

        await PumpAsync(host);

        var row = await RowAsync(host, order.Id);
        Assert.Equal(EmailStatus.Queued, row.Status);
        Assert.Equal(1, row.AttemptCount);
        Assert.NotNull(row.NextAttemptAt);

        var (min, max) = EmailRetryPolicy.Window(1);
        Assert.InRange(row.NextAttemptAt!.Value - host.Clock.GetUtcNow(), min, max);
        Assert.Empty(host.Sender.Sent);
    }

    [Fact]
    public async Task The_schedule_ends_in_Abandoned_rather_than_retrying_forever()
    {
        using var host = await HostAsync();
        host.Sender.Respond = _ => new EmailSendResult(
            EmailSendOutcome.TransientFailure, "451 4.3.0 try later", "4.3.0");

        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host);
        await QueueAsync(host, order.Id);

        for (var attempt = 0; attempt <= EmailRetryPolicy.Schedule.Length; attempt++)
        {
            await PumpAsync(host);
            host.Clock.Advance(TimeSpan.FromHours(24));
        }

        Assert.Equal(EmailStatus.Abandoned, (await RowAsync(host, order.Id)).Status);
    }

    /// <summary>
    /// B15. One hard bounce is not evidence: on the first day of real sending, with DNS
    /// not yet propagated, suppressing on the first rejection would silently blocklist
    /// every customer who bought that day.
    /// </summary>
    [Fact]
    public async Task One_hard_bounce_fails_the_row_but_does_not_suppress_the_address()
    {
        using var host = await HostAsync();
        host.Sender.Respond = _ => new EmailSendResult(
            EmailSendOutcome.PermanentFailure, "550 5.1.1 no such mailbox", "5.1.1");

        var address = $"gone-{Guid.NewGuid():N}@example.com";
        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host, email: address);
        await QueueAsync(host, order.Id);

        await PumpAsync(host);

        var row = await RowAsync(host, order.Id);
        Assert.Equal(EmailStatus.Failed, row.Status);
        Assert.StartsWith(EmailSuppressionList.HardBounceMarker, row.LastError ?? "", StringComparison.Ordinal);
        Assert.False(await IsSuppressedAsync(host, address));
    }

    [Fact]
    public async Task A_second_hard_bounce_on_a_different_day_suppresses_the_address()
    {
        using var host = await HostAsync();
        host.Sender.Respond = _ => new EmailSendResult(
            EmailSendOutcome.PermanentFailure, "550 5.1.1 no such mailbox", "5.1.1");

        var address = $"gone-{Guid.NewGuid():N}@example.com";

        var (first, _, _) = await EmailStageDData.PaidOrderAsync(host, email: address);
        await QueueAsync(host, first.Id);
        await PumpAsync(host);

        host.Clock.Advance(TimeSpan.FromDays(1));

        var (second, _, _) = await EmailStageDData.PaidOrderAsync(host, email: address);
        await QueueAsync(host, second.Id);
        await PumpAsync(host);

        Assert.True(await IsSuppressedAsync(host, address));
    }

    /// <summary>
    /// 5.7.x is returned at RCPT TO exactly as 5.1.1 is, and it means the receiving domain
    /// rejected <em>our</em> sender policy. Suppressing on it would punish the customer
    /// for our DNS.
    /// </summary>
    [Fact]
    public async Task A_sender_policy_rejection_never_suppresses_however_often_it_happens()
    {
        using var host = await HostAsync();
        host.Sender.Respond = _ => new EmailSendResult(
            EmailSendOutcome.PermanentFailure, "550 5.7.26 unauthenticated email not accepted", "5.7.26");

        var address = $"fine-{Guid.NewGuid():N}@example.com";

        for (var day = 0; day < 3; day++)
        {
            var (order, _, _) = await EmailStageDData.PaidOrderAsync(host, email: address);
            await QueueAsync(host, order.Id);
            await PumpAsync(host);
            host.Clock.Advance(TimeSpan.FromDays(1));
        }

        Assert.False(await IsSuppressedAsync(host, address));
    }

    [Fact]
    public async Task An_address_suppressed_after_enqueue_is_not_written_to()
    {
        using var host = await HostAsync();
        var address = $"late-{Guid.NewGuid():N}@example.com";
        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host, email: address);
        await QueueAsync(host, order.Id);

        await host.InScopeAsync(sp => sp.GetRequiredService<EmailSuppressionList>()
            .SuppressAsync(address, SuppressionReason.ManualBlock, null));

        await PumpAsync(host);

        Assert.Equal(EmailStatus.Suppressed, (await RowAsync(host, order.Id)).Status);
        Assert.Empty(host.Sender.Sent);

        // The property that matters: the order is still paid and the grant still works.
        await host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<NexusDbContext>();
            Assert.Equal(OrderStatus.Paid, (await db.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id)).Status);
            Assert.True(await db.DownloadGrants.AnyAsync(g => g.OrderId == order.Id && g.State == GrantState.Active));
        });
    }

    /// <summary>
    /// Every link on the order is dead, so the message is refused rather than sent. A
    /// customer who tries a dead link distrusts the next one too; re-issue comes first.
    /// </summary>
    [Fact]
    public async Task A_message_with_no_live_grant_is_refused_not_sent()
    {
        using var host = await HostAsync();
        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host, withGrant: false);
        await QueueAsync(host, order.Id);

        await PumpAsync(host);

        var row = await RowAsync(host, order.Id);
        Assert.Equal(EmailStatus.Failed, row.Status);
        Assert.Contains("no_live_grants", row.LastError ?? "", StringComparison.Ordinal);
        Assert.Empty(host.Sender.Sent);
    }

    [Fact]
    public async Task An_expired_lease_is_recovered_once_and_failed_on_the_second_expiry()
    {
        using var host = await HostAsync();
        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host);
        await QueueAsync(host, order.Id);

        await StrandAsync(host, order.Id);
        host.Clock.Advance(TimeSpan.FromMinutes(5));
        await host.InScopeAsync(sp => sp.GetRequiredService<OutboxPump>().RecoverExpiredLeasesAsync());

        var recovered = await RowAsync(host, order.Id);
        Assert.Equal(EmailStatus.Queued, recovered.Status);
        Assert.StartsWith(OutboxPump.PossibleDuplicateMarker, recovered.LastError ?? "", StringComparison.Ordinal);

        await StrandAsync(host, order.Id);
        host.Clock.Advance(TimeSpan.FromMinutes(5));
        await host.InScopeAsync(sp => sp.GetRequiredService<OutboxPump>().RecoverExpiredLeasesAsync());

        var dead = await RowAsync(host, order.Id);
        Assert.Equal(EmailStatus.Failed, dead.Status);
        Assert.Equal("lease expired twice", dead.LastError);
    }

    /// <summary>Two dispatchers against one database must not both send the same row.</summary>
    [Fact]
    public async Task Two_dispatchers_claim_disjoint_rows()
    {
        using var host = await HostAsync();
        var orders = new List<int>();

        for (var i = 0; i < 6; i++)
        {
            var (order, _, _) = await EmailStageDData.PaidOrderAsync(host);
            await QueueAsync(host, order.Id);
            orders.Add(order.Id);
        }

        var first = await host.InScopeAsync(sp => sp.GetRequiredService<OutboxPump>().ClaimAsync(3));
        var second = await host.InScopeAsync(sp => sp.GetRequiredService<OutboxPump>().ClaimAsync(10));

        Assert.Equal(3, first.Count);
        Assert.Empty(first.Intersect(second));
        Assert.Equal(orders.Count, first.Count + second.Count);
    }

    [Fact]
    public async Task The_claim_takes_the_oldest_first()
    {
        using var host = await HostAsync();
        var queued = new List<string>();

        for (var i = 0; i < 3; i++)
        {
            var (order, _, _) = await EmailStageDData.PaidOrderAsync(host);
            await QueueAsync(host, order.Id);
            queued.Add((await RowAsync(host, order.Id)).PublicRef);
            host.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        var claimed = await host.InScopeAsync(sp => sp.GetRequiredService<OutboxPump>().ClaimAsync(3));

        var refs = await host.InScopeAsync(sp => sp.GetRequiredService<NexusDbContext>()
            .OutboxEmails.AsNoTracking()
            .Where(e => claimed.Contains(e.Id))
            .OrderBy(e => e.CreatedAt)
            .Select(e => e.PublicRef)
            .ToListAsync());

        Assert.Equal(queued, refs);
    }

    [Fact]
    public async Task A_suppressed_row_is_never_claimed()
    {
        using var host = await HostAsync(s => s["Email:Transport"] = "None");
        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host);
        await QueueAsync(host, order.Id);

        Assert.Empty(await host.InScopeAsync(sp => sp.GetRequiredService<OutboxPump>().ClaimAsync(10)));
    }

    // ---- health ------------------------------------------------------------

    [Fact]
    public async Task A_queue_nobody_is_draining_turns_the_health_check_red()
    {
        using var host = await HostAsync();
        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host);
        await QueueAsync(host, order.Id);

        host.Clock.Advance(TimeSpan.FromMinutes(16));

        var result = await CheckAsync(host);
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("oldest queued", result.Description ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_transport_being_off_is_reported_as_degraded()
    {
        using var host = await HostAsync(s => s["Email:Transport"] = "None");

        var result = await CheckAsync(host);
        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("None", result.Description ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// Health measured only on the queue's age would call this green: the queue is empty
    /// because everything in it failed.
    /// </summary>
    [Fact]
    public async Task Failed_rows_are_counted_even_though_the_queue_is_empty()
    {
        using var host = await HostAsync();
        host.Sender.Respond = _ => new EmailSendResult(
            EmailSendOutcome.PermanentFailure, "550 5.6.0 message rejected", "5.6.0");

        for (var i = 0; i < 5; i++)
        {
            var (order, _, _) = await EmailStageDData.PaidOrderAsync(host);
            await QueueAsync(host, order.Id);
        }

        await PumpAsync(host);

        var result = await CheckAsync(host);
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("failed or were abandoned", result.Description ?? "", StringComparison.Ordinal);
    }

    // §4.4: the dispatcher has no HttpContext, so a base URL that is not absolute ships a
    // relative path into a mail client. The check names the setting rather than leaving
    // somebody to find a dead link in a customer's inbox.
    [Fact]
    public async Task A_base_url_that_is_not_absolute_degrades_the_health_check()
    {
        using var host = await HostAsync(s => s["Site:PublicBaseUrl"] = "/");

        var result = await CheckAsync(host);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("Site:PublicBaseUrl", result.Description ?? "", StringComparison.Ordinal);
    }

    // ---- helpers -----------------------------------------------------------

    private static Task<HealthCheckResult> CheckAsync(EmailStageDHost host) =>
        host.InScopeAsync(sp => sp.GetRequiredService<OutboxHealthCheck>()
            .CheckHealthAsync(new HealthCheckContext()));

    private static Task<int> PumpAsync(EmailStageDHost host) =>
        host.InScopeAsync(sp => sp.GetRequiredService<OutboxPump>().RunOnceAsync());

    /// <summary>Leaves a row mid-send with a lease that is about to expire.</summary>
    private static Task StrandAsync(EmailStageDHost host, int orderId) =>
        host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<NexusDbContext>();
            var row = await db.OutboxEmails.FirstAsync(e => e.OrderId == orderId);
            row.Status = EmailStatus.Sending;
            row.NextAttemptAt = host.Clock.GetUtcNow().AddMinutes(2);
            await db.SaveChangesAsync();
        });

    private static Task<bool> IsSuppressedAsync(EmailStageDHost host, string email) =>
        host.InScopeAsync(sp => sp.GetRequiredService<IEmailSuppressionList>().IsSuppressedAsync(email));

    private static Task QueueAsync(EmailStageDHost host, int orderId) =>
        host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<NexusDbContext>();
            await sp.GetRequiredService<IOrderEmailService>().QueueOrderConfirmationAsync(orderId);
            await db.SaveChangesAsync();
        });

    private static Task<OutboxEmail> RowAsync(EmailStageDHost host, int orderId) =>
        host.InScopeAsync(sp => sp.GetRequiredService<NexusDbContext>()
            .OutboxEmails.AsNoTracking().FirstAsync(e => e.OrderId == orderId));
}
