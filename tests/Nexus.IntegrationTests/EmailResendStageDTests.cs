using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Domain;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Email;
using Nexus.Infrastructure.Persistence;

namespace Nexus.IntegrationTests;

/// <summary>
/// The resend path: the fix for the commonest support call, and the one endpoint a
/// compromised staff account could point at an arbitrary address.
/// </summary>
[Collection(NexusCollection.Name)]
public sealed class EmailResendStageDTests(NexusApp app)
{
    private readonly NexusApp _app = app;

    // Every test starts from an empty outbox: a dispatcher pass claims whatever is queued,
    // including rows an earlier class left behind.
    private static Task<EmailStageDHost> HostAsync(Action<Dictionary<string, string?>>? configure = null) =>
        new EmailStageDHost(configure).ResetAsync();

    [Fact]
    public async Task A_resend_queues_the_links_and_audits_the_actor()
    {
        using var host = await HostAsync();
        var (order, _, token) = await EmailStageDData.PaidOrderAsync(host);

        var outcome = await ResendAsync(host, new ResendRequest(order.Id, ActorId: "admin-1"));

        Assert.True(outcome.Queued);
        Assert.Equal(order.BuyerEmailSnapshot, outcome.To);

        var row = Assert.Single(await RowsAsync(host, order.Id));
        Assert.Equal(EmailKind.DownloadLinks, row.Kind);
        Assert.Equal($"download-links:{order.Id}:0", row.IdempotencyKey);

        Assert.Single(await AuditAsync(host, order.Id, "email.resend.queued"));

        // and it really does carry a working link
        await host.InScopeAsync(sp => sp.GetRequiredService<OutboxPump>().RunOnceAsync());
        Assert.Contains(token, Assert.Single(host.Sender.Sent).Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Three per hour. It protects the customer from a stuck operator clicking a button
    /// that seems not to work, and the sending domain from everything else.
    /// </summary>
    [Fact]
    public async Task The_fourth_resend_within_an_hour_is_refused_with_a_retry_window()
    {
        using var host = await HostAsync();
        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host);

        for (var i = 0; i < 3; i++)
            Assert.True((await ResendAsync(host, new ResendRequest(order.Id, ActorId: "admin-1"))).Queued);

        var refused = await ResendAsync(host, new ResendRequest(order.Id, ActorId: "admin-1"));

        Assert.False(refused.Queued);
        Assert.Equal("rate_limited", refused.Problem);
        Assert.Equal("order", refused.Scope);
        Assert.Equal(3, refused.Limit);
        Assert.Equal(3600, refused.RetryAfterSeconds);
    }

    // A refusal that costs the actor nothing is not a limit, so refusals are audited and
    // count toward the per-actor budget.
    [Fact]
    public async Task A_refused_resend_is_audited()
    {
        using var host = await HostAsync();
        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host, status: OrderStatus.Pending, withGrant: false);

        var refused = await ResendAsync(host, new ResendRequest(order.Id, ActorId: "admin-7"));

        Assert.False(refused.Queued);
        Assert.Equal("order_not_paid", refused.Problem);
        Assert.Single(await AuditAsync(host, order.Id, "email.resend.refused"));
    }

    [Fact]
    public async Task An_actor_firing_at_every_order_is_stopped_across_all_of_them()
    {
        using var host = await HostAsync(s => s["Email:Transport"] = "FileDrop");
        host.Settings.Set(EmailSettingKeys.ResendPerActorPerHour, "2");

        var first = (await EmailStageDData.PaidOrderAsync(host)).Order;
        var second = (await EmailStageDData.PaidOrderAsync(host)).Order;
        var third = (await EmailStageDData.PaidOrderAsync(host)).Order;

        Assert.True((await ResendAsync(host, new ResendRequest(first.Id, ActorId: "staff-9"))).Queued);
        Assert.True((await ResendAsync(host, new ResendRequest(second.Id, ActorId: "staff-9"))).Queued);

        var refused = await ResendAsync(host, new ResendRequest(third.Id, ActorId: "staff-9"));
        Assert.False(refused.Queued);
        Assert.Equal("actor", refused.Scope);
    }

    /// <summary>
    /// Sending a dead link is a worse support outcome than refusing: the customer tries
    /// it, it fails, and now they distrust the next one too. Re-issue comes first, and the
    /// refusal names the books so the operator knows which.
    /// </summary>
    [Fact]
    public async Task A_resend_with_every_grant_expired_is_refused_and_names_the_books()
    {
        using var host = await HostAsync();
        var (order, _, _) = await EmailStageDData.PaidOrderAsync(
            host, expiresAt: host.Clock.GetUtcNow().AddDays(-1));

        var refused = await ResendAsync(host, new ResendRequest(order.Id, ActorId: "admin-1"));

        Assert.False(refused.Queued);
        Assert.Equal("no_live_grants", refused.Problem);
        var dead = Assert.Single(refused.DeadGrants!);
        Assert.Equal("expired", dead.State);
        Assert.False(string.IsNullOrWhiteSpace(dead.TitleEn));
        Assert.False(string.IsNullOrWhiteSpace(dead.TitleAr));
    }

    [Fact]
    public async Task An_exhausted_grant_counts_as_dead()
    {
        using var host = await HostAsync();
        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host, maxDownloads: 3, used: 3);

        var refused = await ResendAsync(host, new ResendRequest(order.Id, ActorId: "admin-1"));

        Assert.Equal("no_live_grants", refused.Problem);
        Assert.Equal("exhausted", Assert.Single(refused.DeadGrants!).State);
    }

    [Fact]
    public async Task A_suppressed_recipient_is_refused_rather_than_queued()
    {
        using var host = await HostAsync();
        var address = $"blocked-{Guid.NewGuid():N}@example.com";
        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host, email: address);

        await host.InScopeAsync(sp => sp.GetRequiredService<EmailSuppressionList>()
            .SuppressAsync(address, SuppressionReason.HardBounce, "5.1.1"));

        var refused = await ResendAsync(host, new ResendRequest(order.Id, ActorId: "admin-1"));

        Assert.Equal("recipient_suppressed", refused.Problem);
        Assert.Empty(await RowsAsync(host, order.Id));
    }

    [Fact]
    public async Task With_the_transport_off_a_resend_says_so_rather_than_queueing_silently()
    {
        using var host = await HostAsync(s => s["Email:Transport"] = "None");
        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host);

        Assert.Equal("email_disabled", (await ResendAsync(host, new ResendRequest(order.Id))).Problem);
    }

    [Fact]
    public async Task An_unknown_language_is_refused_rather_than_silently_answered_in_English()
    {
        using var host = await HostAsync();
        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host, language: "ar");

        Assert.Equal("invalid_language",
            (await ResendAsync(host, new ResendRequest(order.Id, Language: "fr"))).Problem);
    }

    [Fact]
    public async Task The_operator_can_correct_a_typed_address_and_it_is_recorded()
    {
        using var host = await HostAsync();
        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host);
        var corrected = $"corrected-{Guid.NewGuid():N}@example.com";

        var outcome = await ResendAsync(host, new ResendRequest(order.Id, ToOverride: corrected, ActorId: "admin-1"));

        Assert.True(outcome.Queued);
        Assert.Equal(corrected, outcome.To);
        Assert.Equal(corrected, Assert.Single(await RowsAsync(host, order.Id)).ToEmail);

        // the order itself is untouched
        await host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<NexusDbContext>();
            var reloaded = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
            Assert.Equal(order.BuyerEmailSnapshot, reloaded.BuyerEmailSnapshot);
        });
    }

    [Fact]
    public async Task A_resend_in_the_other_language_renders_in_that_language()
    {
        using var host = await HostAsync();
        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host, language: "en");

        Assert.True((await ResendAsync(host, new ResendRequest(order.Id, Language: "ar"))).Queued);
        await host.InScopeAsync(sp => sp.GetRequiredService<OutboxPump>().RunOnceAsync());

        var message = Assert.Single(host.Sender.Sent);
        Assert.Contains("رابط التحميل", message.Subject, StringComparison.Ordinal);
        Assert.Contains("لم يتم خصم أي مبلغ", message.Text, StringComparison.Ordinal);
        Assert.Contains("dir=\"rtl\"", message.Html, StringComparison.Ordinal);
    }

    private static Task<ResendOutcome> ResendAsync(EmailStageDHost host, ResendRequest request) =>
        host.InScopeAsync(sp => sp.GetRequiredService<IAdminEmailOperations>().ResendAsync(request));

    private static Task<List<OutboxEmail>> RowsAsync(EmailStageDHost host, int orderId) =>
        host.InScopeAsync(sp => sp.GetRequiredService<NexusDbContext>()
            .OutboxEmails.AsNoTracking().Where(e => e.OrderId == orderId).ToListAsync());

    private static Task<List<AuditLog>> AuditAsync(EmailStageDHost host, int orderId, string action) =>
        host.InScopeAsync(sp => sp.GetRequiredService<NexusDbContext>()
            .AuditLogs.AsNoTracking()
            .Where(a => a.Entity == "OutboxEmail" && a.EntityId == orderId.ToString() && a.Action == action)
            .ToListAsync());
}
