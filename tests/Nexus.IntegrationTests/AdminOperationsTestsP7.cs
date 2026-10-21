using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexus.Application.Common;
using Nexus.Domain;
using Nexus.Domain.Entities;

namespace Nexus.IntegrationTests;

/// <summary>
/// The running-the-store screens: the dashboard's numbers, the settings, the delivery
/// log, the customer record, and the refund that honestly refuses.
/// </summary>
[Collection(NexusCollection.Name)]
public sealed class AdminOperationsTestsP7(NexusApp app) : IDisposable
{
    private readonly AdminHostP7 _host = new(app);

    public void Dispose() => _host.Dispose();

    // ---- dashboard ----------------------------------------------------------

    [Fact]
    public async Task The_dashboard_counts_rather_than_estimates()
    {
        var body = await _host.AsStaff().GetJsonAsync("/admin/api/dashboard");

        Assert.Equal(30, body.GetProperty("trend").GetArrayLength());

        using var scope = _host.Scope();
        var db = _host.Db(scope);

        Assert.Equal(await db.Books.CountAsync(b => b.IsPublished), body.GetProperty("titlesLive").GetInt32());
        Assert.Equal(await db.Books.CountAsync(), body.GetProperty("titlesTotal").GetInt32());
        Assert.Equal(await db.Customers.CountAsync(), body.GetProperty("customersTotal").GetInt32());

        var from = DateTimeOffset.UtcNow.AddDays(-30);
        var expected = await db.Orders
            .Where(o => o.PaidAt != null && o.PaidAt >= from &&
                        (o.Status == OrderStatus.Paid || o.Status == OrderStatus.PartiallyRefunded))
            .SumAsync(o => (decimal?)(o.TotalCharged - o.RefundedTotalKwd)) ?? 0m;

        Assert.Equal(expected, body.GetProperty("revenueKwd30").GetDecimal());
    }

    /// <summary>
    /// KWD is a three-decimal currency, and the dashboard total is one of the places a
    /// rounding mistake would be invisible: 9.125 shown as 9.13 looks perfectly normal.
    /// </summary>
    [Fact]
    public async Task Revenue_keeps_all_three_decimals()
    {
        var client = _host.AsStaff();
        var before = (await client.GetJsonAsync("/admin/api/dashboard"))
            .GetProperty("revenueKwd30").GetDecimal();

        var order = await PaidOrderAsync("fils", 9.125m);
        try
        {
            var after = (await client.GetJsonAsync("/admin/api/dashboard"))
                .GetProperty("revenueKwd30").GetDecimal();

            // Not "about nine dinars": exactly nine point one two five. A fils lost here
            // is a fils the operator reconciles against the bank and cannot find.
            Assert.Equal(9.125m, after - before);
            Assert.Equal(after, Currency.Kwd.Round(after));
        }
        finally
        {
            await RemoveOrderAsync(order);
        }
    }

    // ---- settings -----------------------------------------------------------

    [Fact]
    public async Task A_setting_is_written_through_the_reader_and_audited()
    {
        var client = _host.AsAdmin();

        /* Deliberately not linkExpiryDays or maxDownloads. Those two are on the public
         * allow-list, other tests assert their seeded values through /api/settings/public,
         * and that endpoint is served by a different host with its own sixty-second
         * cache — so changing one here and changing it back can still be visible over
         * there for a minute. This key is read by nothing else. */
        var before = await CurrentThresholdAsync();
        var target = before == 9 ? 8 : 9;

        try
        {
            var response = await client.PutJsonAsync(
                "/admin/api/settings/delivery.distinctIpThreshold", new { value = target.ToString() });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(target, await CurrentThresholdAsync());

            using var scope = _host.Scope();
            Assert.True(await _host.Db(scope).AuditLogs.AnyAsync(a =>
                a.Entity == "Setting" && a.EntityId == "delivery.distinctIpThreshold" &&
                a.Action == "setting.update"));
        }
        finally
        {
            await client.PutJsonAsync("/admin/api/settings/delivery.distinctIpThreshold",
                new { value = before.ToString() });
        }
    }

    [Fact]
    public async Task A_value_outside_the_allowed_range_is_refused()
    {
        var response = await _host.AsAdmin().PutJsonAsync(
            "/admin/api/settings/delivery.maxDownloads", new { value = "9999" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("out_of_range", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The allow-list is the list in code. The same table will hold the gateway keys, and
    /// a settings endpoint that writes whatever key it is handed is a way to set one from
    /// a browser.
    /// </summary>
    [Fact]
    public async Task A_key_that_is_not_on_the_allow_list_cannot_be_written()
    {
        var response = await _host.AsAdmin().PutJsonAsync(
            "/admin/api/settings/payments.tap.secretKey", new { value = "anything-at-all" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = _host.Scope();
        Assert.False(await _host.Db(scope).Settings.AnyAsync(s => s.Key == "payments.tap.secretKey"));
    }

    [Fact]
    public async Task A_secret_is_listed_but_its_value_never_leaves_the_server()
    {
        const string key = "p7.test.secret";
        const string value = "a-value-only-the-server-should-see";

        using (var scope = _host.Scope())
        {
            var db = _host.Db(scope);
            db.Settings.Add(new Setting
            {
                Key = key, Value = value, IsSecret = true, UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        try
        {
            var response = await _host.AsAdmin().GetAsync("/admin/api/settings");
            var raw = await response.Content.ReadAsStringAsync();

            Assert.DoesNotContain(value, raw, StringComparison.Ordinal);

            var row = JsonDocument.Parse(raw).RootElement.EnumerateArray()
                .Single(r => r.GetProperty("key").GetString() == key);

            Assert.True(row.GetProperty("isSecret").GetBoolean());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("value").ValueKind);
            // Listed, so the operator knows it exists and that something is set.
            Assert.True(row.GetProperty("isSet").GetBoolean());
        }
        finally
        {
            using var scope = _host.Scope();
            var db = _host.Db(scope);
            db.Settings.RemoveRange(await db.Settings.Where(s => s.Key == key).ToListAsync());
            await db.SaveChangesAsync();
        }
    }

    // ---- downloads ----------------------------------------------------------

    [Fact]
    public async Task No_download_endpoint_hands_back_a_token()
    {
        var (order, grantId, token) = await GrantAsync("tokens");
        try
        {
            var list = await _host.AsStaff().GetAsync("/admin/api/downloads");
            var log = await _host.AsStaff().GetAsync($"/admin/api/downloads/{grantId}/log");

            foreach (var response in new[] { list, log })
            {
                var raw = await response.Content.ReadAsStringAsync();
                Assert.DoesNotContain(token, raw, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("tokenHash", raw, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("tokenProtected", raw, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            await RemoveOrderAsync(order);
        }
    }

    [Fact]
    public async Task Revoking_a_grant_kills_that_link_and_leaves_the_rest_alone()
    {
        var (order, grantId, _) = await GrantAsync("revoke");
        try
        {
            var response = await _host.AsStaff().PostJsonAsync(
                $"/admin/api/downloads/{grantId}/revoke", new { reason = "AbuseSuspected" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var scope = _host.Scope();
            var db = _host.Db(scope);
            var grant = await db.DownloadGrants.AsNoTracking().FirstAsync(g => g.Id == grantId);

            Assert.Equal(GrantState.Revoked, grant.State);
            Assert.Equal(RevocationReason.AbuseSuspected, grant.RevocationReason);
            // The one copy of a working link we hold goes with it.
            Assert.Null(grant.TokenProtected);

            var key = grantId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(await db.AuditLogs.AnyAsync(a =>
                a.Entity == "DownloadGrant" && a.EntityId == key && a.Action == "grant.revoke"));
        }
        finally
        {
            await RemoveOrderAsync(order);
        }
    }

    [Fact]
    public async Task Revoking_twice_is_refused_rather_than_silently_repeated()
    {
        var (order, grantId, _) = await GrantAsync("revoke-twice");
        try
        {
            var client = _host.AsStaff();
            await client.PostJsonAsync($"/admin/api/downloads/{grantId}/revoke", new { });
            var again = await client.PostJsonAsync($"/admin/api/downloads/{grantId}/revoke", new { });

            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Contains("grant_already_revoked", await again.Content.ReadAsStringAsync());
        }
        finally
        {
            await RemoveOrderAsync(order);
        }
    }

    [Fact]
    public async Task Re_issuing_replaces_the_link_and_returns_no_token()
    {
        var (order, grantId, token) = await GrantAsync("reissue");
        try
        {
            var response = await _host.AsStaff().PostJsonAsync($"/admin/api/downloads/{grantId}/reissue", new { });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain(token, await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

            using var scope = _host.Scope();
            var db = _host.Db(scope);

            var old = await db.DownloadGrants.AsNoTracking().FirstAsync(g => g.Id == grantId);
            Assert.Equal(GrantState.Revoked, old.State);
            Assert.Equal(RevocationReason.ReIssued, old.RevocationReason);

            var fresh = await db.DownloadGrants.AsNoTracking()
                .Where(g => g.OrderId == order && g.Id != grantId)
                .SingleAsync();
            Assert.Equal(GrantState.Active, fresh.State);
            Assert.Equal(2, fresh.Sequence);
        }
        finally
        {
            await RemoveOrderAsync(order);
        }
    }

    // ---- orders and refunds --------------------------------------------------

    [Fact]
    public async Task A_refund_is_offered_and_honestly_refused_while_the_gateway_is_held()
    {
        var order = await PaidOrderAsync("refund", 6.500m);
        try
        {
            var response = await _host.AsAdmin().PostJsonAsync(
                $"/admin/api/orders/{order}/refund", new { amountKwd = 6.500m, reason = "duplicate" });

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("payments_held", await response.Content.ReadAsStringAsync());

            using var scope = _host.Scope();
            var db = _host.Db(scope);

            // Nothing moved, and nothing pretends it did.
            var row = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == order);
            Assert.Equal(OrderStatus.Paid, row.Status);
            Assert.Equal(0m, row.RefundedTotalKwd);
            Assert.False(await db.Refunds.AnyAsync(r => r.OrderId == order));

            // But the attempt is answerable.
            var key = order.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(await db.AuditLogs.AnyAsync(a =>
                a.Entity == "Order" && a.EntityId == key && a.Action == "order.refund.refused"));
        }
        finally
        {
            await RemoveOrderAsync(order);
        }
    }

    [Fact]
    public async Task A_refund_larger_than_the_order_is_refused_before_the_gateway_is_consulted()
    {
        var order = await PaidOrderAsync("overrefund", 6.500m);
        try
        {
            var response = await _host.AsAdmin().PostJsonAsync(
                $"/admin/api/orders/{order}/refund", new { amountKwd = 99.000m, reason = "typo" });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("refund_amount_out_of_range", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await RemoveOrderAsync(order);
        }
    }

    [Fact]
    public async Task The_order_detail_shows_the_snapshot_not_the_customer_row()
    {
        var order = await PaidOrderAsync("snapshot", 6.500m);
        try
        {
            using (var scope = _host.Scope())
            {
                var db = _host.Db(scope);
                var row = await db.Orders.FirstAsync(o => o.Id == order);
                var customer = await db.Customers.FirstAsync(c => c.Id == row.CustomerId);
                customer.Name = "Renamed Later";
                await db.SaveChangesAsync();
            }

            var body = await _host.AsStaff().GetJsonAsync($"/admin/api/orders/{order}");

            Assert.Equal("Buyer", body.GetProperty("order").GetProperty("buyerName").GetString());
        }
        finally
        {
            await RemoveOrderAsync(order);
        }
    }

    // ---- customers -----------------------------------------------------------

    [Fact]
    public async Task Erasing_a_customer_keeps_the_money_and_removes_the_person()
    {
        var order = await PaidOrderAsync("erase", 6.500m);
        int customerId;

        using (var scope = _host.Scope())
        {
            var db = _host.Db(scope);
            customerId = (await db.Orders.AsNoTracking().FirstAsync(o => o.Id == order)).CustomerId;

            // A returning reader with an account, which is the case where erasure has to
            // reach further than the Customer row.
            db.Users.Add(new NexusUser
            {
                UserName = "p7-erase@example.com",
                NormalizedUserName = "P7-ERASE@EXAMPLE.COM",
                Email = "p7-erase@example.com",
                NormalizedEmail = "P7-ERASE@EXAMPLE.COM",
                SecurityStamp = Guid.NewGuid().ToString("N"),
                CustomerId = customerId,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        try
        {
            var response = await _host.AsAdmin().DeleteAsync($"/admin/api/customers/{customerId}");
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

            using var scope = _host.Scope();
            var db = _host.Db(scope);

            var customer = await db.Customers.AsNoTracking().FirstAsync(c => c.Id == customerId);

            // The address they gave us is gone, and what replaces it is on a domain
            // RFC 2606 reserves, so nothing we kept can ever be mailed to a real person.
            Assert.DoesNotContain("@example.com", customer.Email, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("@removed.invalid", customer.Email, StringComparison.Ordinal);
            Assert.Equal("Erased", customer.Name);

            // The accounting record survives, with the person taken out of it.
            var row = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == order);
            Assert.Equal(6.500m, row.TotalCharged);
            Assert.Equal(customer.Email, row.BuyerEmailSnapshot);

            Assert.All(await db.DownloadGrants.AsNoTracking().Where(g => g.OrderId == order).ToListAsync(),
                g => Assert.Equal(GrantState.Revoked, g.State));

            // The login is gone too: an address left in AspNetUsers can still sign in and
            // can still be sent a password-reset mail.
            Assert.False(await db.Users.AnyAsync(u => u.CustomerId == customerId));
            Assert.False(await db.Users.AnyAsync(u => u.NormalizedEmail == "P7-ERASE@EXAMPLE.COM"));
        }
        finally
        {
            await RemoveOrderAsync(order);
        }
    }

    [Fact]
    public async Task Lifetime_value_counts_what_was_captured_not_what_was_ordered()
    {
        var paid = await PaidOrderAsync("ltv", 6.500m);
        int customerId;

        using (var scope = _host.Scope())
            customerId = (await _host.Db(scope).Orders.AsNoTracking().FirstAsync(o => o.Id == paid)).CustomerId;

        var pending = await PendingOrderAsync(customerId, 4.250m);

        try
        {
            var body = await _host.AsStaff().GetJsonAsync($"/admin/api/customers/{customerId}");
            var customer = body.GetProperty("customer");

            Assert.Equal(6.500m, customer.GetProperty("lifetimeValueKwd").GetDecimal());
            Assert.Equal(2, customer.GetProperty("orderCount").GetInt32());
            Assert.Equal(1, customer.GetProperty("paidOrderCount").GetInt32());
        }
        finally
        {
            await RemoveOrderAsync(paid, pending);
        }
    }

    // ---- fixtures -------------------------------------------------------------

    private async Task<int> CurrentThresholdAsync()
    {
        using var scope = _host.Scope();
        return await _host.Service<ISettingsReader>(scope)
            .GetIntAsync(SettingKeys.DistinctIpThreshold, 4, 2, 50);
    }

    private async Task<int> PaidOrderAsync(string tag, decimal price, int bookId = 1)
    {
        using var scope = _host.Scope();
        var db = _host.Db(scope);
        var book = await db.Books.AsNoTracking().FirstAsync(b => b.Id == bookId);
        var now = DateTimeOffset.UtcNow;

        var email = $"p7-{tag}@example.com";
        var customer = new Customer
        {
            Email = email,
            NormalisedEmail = Customer.Normalise(email),
            Name = "Buyer",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        var order = new Order
        {
            OrderNumber = $"KW-P7-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            CustomerId = customer.Id,
            BuyerNameSnapshot = "Buyer",
            BuyerEmailSnapshot = email,
            Status = OrderStatus.Paid,
            Language = "en",
            SubtotalKwd = price,
            TotalKwd = price,
            TotalCharged = price,
            PaymentExpiresAt = now.AddHours(24),
            PaidAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            Items =
            [
                new OrderItem
                {
                    BookId = book.Id,
                    UnitPriceKwd = price,
                    TitleEnSnapshot = book.TitleEn,
                    TitleArSnapshot = book.TitleAr,
                    AuthorEnSnapshot = book.AuthorEn,
                    AuthorArSnapshot = book.AuthorAr
                }
            ]
        };

        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }

    private async Task<int> PendingOrderAsync(int customerId, decimal price, int bookId = 2)
    {
        using var scope = _host.Scope();
        var db = _host.Db(scope);
        var book = await db.Books.AsNoTracking().FirstAsync(b => b.Id == bookId);
        var now = DateTimeOffset.UtcNow;

        var order = new Order
        {
            OrderNumber = $"KW-P7-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            CustomerId = customerId,
            BuyerNameSnapshot = "Buyer",
            BuyerEmailSnapshot = "p7-pending@example.com",
            Status = OrderStatus.Pending,
            Language = "en",
            SubtotalKwd = price,
            TotalKwd = price,
            TotalCharged = price,
            PaymentExpiresAt = now.AddHours(24),
            CreatedAt = now,
            UpdatedAt = now,
            Items =
            [
                new OrderItem
                {
                    BookId = book.Id,
                    UnitPriceKwd = price,
                    TitleEnSnapshot = book.TitleEn,
                    TitleArSnapshot = book.TitleAr,
                    AuthorEnSnapshot = book.AuthorEn,
                    AuthorArSnapshot = book.AuthorAr
                }
            ]
        };

        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }

    /// <summary>A paid order with a real file and a live grant behind it.</summary>
    private async Task<(int Order, int Grant, string Token)> GrantAsync(string tag)
    {
        var order = await PaidOrderAsync(tag, 6.500m);

        using var scope = _host.Scope();
        await _host.Service<Nexus.Application.Delivery.IPdfLibrary>(scope).WritePlaceholderAsync(1);

        var issued = await _host.Service<Nexus.Application.Delivery.IDeliveryService>(scope)
            .IssueGrantsForOrderAsync(order);

        return (order, issued[0].Grant.Id, issued[0].Token);
    }

    private async Task RemoveOrderAsync(params int[] orderIds)
    {
        using var scope = _host.Scope();
        var db = _host.Db(scope);

        var grants = await db.DownloadGrants.Where(g => orderIds.Contains(g.OrderId)).ToListAsync();
        var grantIds = grants.Select(g => g.Id).ToList();

        db.DownloadLogs.RemoveRange(await db.DownloadLogs.Where(l => grantIds.Contains(l.GrantId)).ToListAsync());
        db.DownloadGrants.RemoveRange(grants);
        db.OutboxEmails.RemoveRange(await db.OutboxEmails
            .Where(e => e.OrderId != null && orderIds.Contains(e.OrderId.Value)).ToListAsync());
        db.OrderEvents.RemoveRange(await db.OrderEvents.Where(e => orderIds.Contains(e.OrderId)).ToListAsync());
        db.OrderItems.RemoveRange(await db.OrderItems.Where(i => orderIds.Contains(i.OrderId)).ToListAsync());

        var orders = await db.Orders.Where(o => orderIds.Contains(o.Id)).ToListAsync();
        var customerIds = orders.Select(o => o.CustomerId).Distinct().ToList();
        db.Orders.RemoveRange(orders);
        await db.SaveChangesAsync();

        var orphans = await db.Customers
            .Where(c => customerIds.Contains(c.Id) && !c.Orders.Any())
            .ToListAsync();
        db.Customers.RemoveRange(orphans);
        await db.SaveChangesAsync();
    }
}
