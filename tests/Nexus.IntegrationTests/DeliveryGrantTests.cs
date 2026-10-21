using Microsoft.EntityFrameworkCore;
using Nexus.Application.Common;
using Nexus.Application.Delivery;
using Nexus.Domain;
using Nexus.Infrastructure.Persistence;
using Nexus.Infrastructure.Settings;

namespace Nexus.IntegrationTests;

/// <summary>Issue, revoke and re-issue, at the service seam the payment code calls.</summary>
[Collection(NexusCollection.Name)]
public sealed class DeliveryGrantTests(NexusApp app) : IDisposable
{
    private readonly DeliveryHost _host = new(app);

    public void Dispose() => _host.Dispose();

    /// <summary>
    /// The webhook is replayed routinely, and this is the one place where "it ran twice"
    /// means somebody gets six downloads instead of three.
    /// </summary>
    [Fact]
    public async Task Issuing_twice_for_one_order_produces_one_grant_per_item()
    {
        var (order, _) = await _host.PaidOrderAsync("gra001", 9);

        using var scope = _host.Scope();
        var delivery = _host.Service<IDeliveryService>(scope);

        var first = await delivery.IssueGrantsForOrderAsync(order.Id);
        var second = await delivery.IssueGrantsForOrderAsync(order.Id);

        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal(first[0].Grant.Id, second[0].Grant.Id);

        var db = _host.Service<NexusDbContext>(scope);
        Assert.Equal(1, await db.DownloadGrants.CountAsync(g => g.OrderId == order.Id));
    }

    /// <summary>
    /// The plaintext is handed back once at issue. Afterwards it exists only encrypted,
    /// and the hash is the only thing any query touches.
    /// </summary>
    [Fact]
    public async Task The_token_is_recoverable_only_through_data_protection()
    {
        var (order, _) = await _host.PaidOrderAsync("grt001", 10);

        using var scope = _host.Scope();
        var delivery = _host.Service<IDeliveryService>(scope);
        var issued = (await delivery.IssueGrantsForOrderAsync(order.Id)).Single();

        var db = _host.Service<NexusDbContext>(scope);
        var stored = await db.DownloadGrants.AsNoTracking().FirstAsync(g => g.Id == issued.Grant.Id);

        Assert.NotNull(stored.TokenProtected);
        Assert.DoesNotContain(issued.Token, Convert.ToBase64String(stored.TokenProtected!), StringComparison.Ordinal);
        Assert.NotEqual(issued.Token, stored.TokenHash);
        Assert.Equal(issued.Token, delivery.UnprotectToken(stored));
    }

    /// <summary>
    /// Revocation is authoritative in the database; the cache purge is housekeeping. A
    /// file the operating system will not let go of must never be able to roll back a
    /// refund.
    /// </summary>
    [Fact]
    public async Task Revoking_an_order_touches_the_database_only()
    {
        var (order, _) = await _host.PaidOrderAsync("grv001", 11);

        using var scope = _host.Scope();
        var delivery = _host.Service<IDeliveryService>(scope);
        await delivery.IssueGrantsForOrderAsync(order.Id);

        var revoked = await delivery.RevokeOrderGrantsAsync(order.Id, RevocationReason.Refunded);
        Assert.Equal(1, revoked);

        var db = _host.Service<NexusDbContext>(scope);
        var grant = await db.DownloadGrants.AsNoTracking().FirstAsync(g => g.OrderId == order.Id);

        Assert.Equal(GrantState.Revoked, grant.State);
        Assert.Equal(RevocationReason.Refunded, grant.RevocationReason);
        Assert.NotNull(grant.RevokedAt);

        // Idempotent: a retried refund webhook finds nothing left to revoke.
        Assert.Equal(0, await delivery.RevokeOrderGrantsAsync(order.Id, RevocationReason.Refunded));
    }

    [Fact]
    public async Task Re_issue_rotates_the_token_and_keeps_the_old_row_as_history()
    {
        var (order, _) = await _host.PaidOrderAsync("gri001", 12);

        using var scope = _host.Scope();
        var delivery = _host.Service<IDeliveryService>(scope);
        var original = (await delivery.IssueGrantsForOrderAsync(order.Id)).Single();

        var (fresh, problem) = await delivery.ReIssueAsync(original.Grant.Id);

        Assert.Null(problem);
        Assert.NotNull(fresh);
        Assert.NotEqual(original.Token, fresh!.Token);
        Assert.Equal(2, fresh.Grant.Sequence);

        var db = _host.Service<NexusDbContext>(scope);
        var old = await db.DownloadGrants.AsNoTracking().FirstAsync(g => g.Id == original.Grant.Id);

        Assert.Equal(GrantState.Revoked, old.State);
        Assert.Equal(RevocationReason.ReIssued, old.RevocationReason);
    }

    [Fact]
    public async Task Re_issue_stops_at_the_configured_limit()
    {
        var (order, _) = await _host.PaidOrderAsync("grl001", 13);

        using var scope = _host.Scope();
        var settings = _host.Service<ISettingsReader>(scope);
        await settings.SetAsync(SettingKeys.ReissueLimitPerItem, "2", false, "test");

        var delivery = _host.Service<IDeliveryService>(scope);
        var current = (await delivery.IssueGrantsForOrderAsync(order.Id)).Single();

        var (second, firstProblem) = await delivery.ReIssueAsync(current.Grant.Id);
        Assert.Null(firstProblem);

        var (third, limitProblem) = await delivery.ReIssueAsync(second!.Grant.Id);

        Assert.Null(third);
        Assert.Equal("reissue_limit", limitProblem);

        await settings.SetAsync(
            SettingKeys.ReissueLimitPerItem,
            DeliveryDefaults.ReissueLimitPerItem.ToString(System.Globalization.CultureInfo.InvariantCulture),
            false, "test");
    }

    /// <summary>
    /// A title withdrawn between order and capture. The money has moved, so the order
    /// stays paid — but we do not hand over something we may no longer have the rights
    /// to, and the order is flagged for a human.
    /// </summary>
    [Fact]
    public async Task A_withdrawn_title_gets_no_grant_and_flags_the_order()
    {
        var (order, book) = await _host.PaidOrderAsync("grw001", 14);

        using var scope = _host.Scope();
        var db = _host.Service<NexusDbContext>(scope);
        var tracked = await db.Books.FirstAsync(b => b.Id == book.Id);
        tracked.IsPublished = false;
        await db.SaveChangesAsync();

        var issued = await _host.Service<IDeliveryService>(scope).IssueGrantsForOrderAsync(order.Id);

        Assert.Empty(issued);
        var reloaded = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
        Assert.Equal($"withdrawn-title:{book.Id}", reloaded.ReviewReason);

        tracked.IsPublished = true;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Snapshotted at issue: an operator who shortens the window from seven days to
    /// three must not retroactively shrink an allowance somebody already paid for.
    /// </summary>
    [Fact]
    public async Task The_allowance_is_snapshotted_and_a_later_edit_does_not_move_it()
    {
        var (order, _) = await _host.PaidOrderAsync("grs001", 15);

        using var scope = _host.Scope();
        var settings = _host.Service<ISettingsReader>(scope);
        await settings.SetAsync(SettingKeys.MaxDownloads, "5", false, "test");

        var issued = (await _host.Service<IDeliveryService>(scope).IssueGrantsForOrderAsync(order.Id)).Single();
        Assert.Equal(5, issued.Grant.MaxDownloads);

        await settings.SetAsync(SettingKeys.MaxDownloads, "1", false, "test");

        var db = _host.Service<NexusDbContext>(scope);
        var reread = await db.DownloadGrants.AsNoTracking().FirstAsync(g => g.Id == issued.Grant.Id);
        Assert.Equal(5, reread.MaxDownloads);

        await settings.SetAsync(
            SettingKeys.MaxDownloads,
            DeliveryDefaults.MaxDownloads.ToString(System.Globalization.CultureInfo.InvariantCulture),
            false, "test");
    }
}
