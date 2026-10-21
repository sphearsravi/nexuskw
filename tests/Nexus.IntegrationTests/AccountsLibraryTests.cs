using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexus.Domain;
using Nexus.Infrastructure.Persistence;

namespace Nexus.IntegrationTests;

/// <summary>
/// AC-3, AC-4 and AC-5, and the rule that outranks all three: a customer sees their own
/// things and nobody else's.
/// </summary>
[Collection(NexusCollection.Name)]
public sealed class AccountsLibraryTests(NexusApp app) : IDisposable
{
    private readonly AccountsHost _host = new(app);
    private const string Password = "correct-horse-battery";

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task The_library_lists_the_grants_with_their_counter_and_a_download_link()
    {
        const string email = "lib-owner@example.com";
        var order = await _host.PaidGuestOrderAsync(email, "Aisha Al-Sane", "LIBA", bookId: 8);
        var (client, _) = await _host.SignedInCustomerAsync(email, "Aisha Al-Sane", Password);

        var items = await ArrayAsync(client, "/api/me/library");
        var item = Assert.Single(items);

        Assert.Equal(order.OrderNumber, item.GetProperty("orderNumber").GetString());
        Assert.Equal("Active", item.GetProperty("state").GetString());
        Assert.Equal(0, item.GetProperty("used").GetInt32());
        Assert.True(item.GetProperty("left").GetInt32() > 0);

        // The landing page, never /download/{token}: a mail scanner following a link
        // must not be able to spend one of the buyer's downloads.
        var url = item.GetProperty("downloadUrl").GetString();
        Assert.StartsWith("/download.html#", url, StringComparison.Ordinal);

        // Both titles travel, so switching language needs no round trip.
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("titleEn").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("titleAr").GetString()));
    }

    /// <summary>
    /// The ownership test. Two customers, one grant each; neither can reach the other's
    /// by id, and the refusal is a 404 rather than a 403 so it does not confirm that the
    /// grant exists.
    /// </summary>
    [Fact]
    public async Task A_customer_cannot_reach_another_customers_grant_by_id()
    {
        await _host.PaidGuestOrderAsync("lib-victim@example.com", "Victim", "LIBV", bookId: 9);
        var (victim, _) = await _host.SignedInCustomerAsync("lib-victim@example.com", "Victim", Password);

        var theirGrant = (await ArrayAsync(victim, "/api/me/library"))[0].GetProperty("grantId").GetInt32();

        await _host.PaidGuestOrderAsync("lib-other@example.com", "Other", "LIBO", bookId: 10);
        var (other, _) = await _host.SignedInCustomerAsync("lib-other@example.com", "Other", Password);

        var response = await other.PostAsync($"/api/me/library/{theirGrant}/reissue", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // And the victim's grant is untouched: no new row, no new sequence.
        var still = await ArrayAsync(victim, "/api/me/library");
        Assert.Equal(theirGrant, still[0].GetProperty("grantId").GetInt32());
    }

    [Fact]
    public async Task A_customer_cannot_read_another_customers_invoice()
    {
        var theirs = await _host.PaidGuestOrderAsync("lib-inv-victim@example.com", "Victim", "LIBIV", bookId: 11);
        await _host.SignedInCustomerAsync("lib-inv-victim@example.com", "Victim", Password);

        await _host.PaidGuestOrderAsync("lib-inv-other@example.com", "Other", "LIBIO", bookId: 12);
        var (other, _) = await _host.SignedInCustomerAsync("lib-inv-other@example.com", "Other", Password);

        var response = await other.GetAsync($"/api/me/orders/{theirs.OrderNumber}");

        // 404 and not 403: distinguishing them would confirm that this order number
        // belongs to somebody.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_invoice_carries_the_orders_own_snapshots_at_three_decimals()
    {
        const string email = "lib-invoice@example.com";
        var order = await _host.PaidGuestOrderAsync(email, "Ghanim Al-Otaibi", "LIBINV", bookId: 13);
        var (client, _) = await _host.SignedInCustomerAsync(email, "Different Name Now", Password);

        var response = await client.GetAsync($"/api/me/orders/{order.OrderNumber}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var invoice = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        // The buyer named on the invoice is the one the order captured, not the name the
        // account happens to carry today.
        Assert.Equal("Ghanim Al-Otaibi", invoice.GetProperty("buyerName").GetString());
        Assert.Equal("KWD", invoice.GetProperty("currency").GetString());
        Assert.Equal(0m, invoice.GetProperty("vatAmount").GetDecimal());

        var total = invoice.GetProperty("totalKwd").GetDecimal();
        Assert.Equal(order.TotalKwd, total);

        // KWD is a three-decimal currency, and the serialised figure must survive the
        // round trip at that scale: 6.5 where 6.500 was charged is wrong by 500 fils.
        Assert.Equal(Currency.Kwd.Format(order.TotalKwd), Currency.Kwd.Format(total));
        Assert.Equal(3, Currency.Kwd.Format(total).Split('.')[1].Length);
    }

    [Fact]
    public async Task Order_history_is_newest_first()
    {
        const string email = "lib-history@example.com";
        await _host.PaidGuestOrderAsync(email, "Dalal Al-Awadhi", "LIBH1", bookId: 14);
        await _host.PaidGuestOrderAsync(email, "Dalal Al-Awadhi", "LIBH2", bookId: 15);
        var (client, _) = await _host.SignedInCustomerAsync(email, "Dalal Al-Awadhi", Password);

        var orders = await ArrayAsync(client, "/api/me/orders");
        Assert.Equal(2, orders.Length);

        var dates = orders.Select(o => o.GetProperty("placedAt").GetDateTimeOffset()).ToList();
        Assert.True(dates[0] >= dates[1]);
    }

    /// <summary>AC-4: an expired link is renewed by its owner, without a support request.</summary>
    [Fact]
    public async Task The_owner_can_reissue_an_expired_link()
    {
        const string email = "lib-reissue@example.com";
        var order = await _host.PaidGuestOrderAsync(email, "Hamad Al-Shatti", "LIBRI", bookId: 16);
        var (client, _) = await _host.SignedInCustomerAsync(email, "Hamad Al-Shatti", Password);

        using (var scope = _host.Scope())
        {
            var db = _host.Service<NexusDbContext>(scope);
            var grant = await db.DownloadGrants.FirstAsync(g => g.OrderId == order.Id);
            grant.ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
        }

        var stale = (await ArrayAsync(client, "/api/me/library"))[0];
        Assert.Equal("expired", stale.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, stale.GetProperty("downloadUrl").ValueKind);
        Assert.True(stale.GetProperty("canReIssue").GetBoolean());

        var reissued = await client.PostAsync(
            $"/api/me/library/{stale.GetProperty("grantId").GetInt32()}/reissue", null);
        Assert.Equal(HttpStatusCode.OK, reissued.StatusCode);

        // One row per book still: the superseded grant is history, not a second shelf
        // entry offering a link that is dead by design.
        var fresh = await ArrayAsync(client, "/api/me/library");
        var item = Assert.Single(fresh);
        Assert.Equal("Active", item.GetProperty("state").GetString());
        Assert.StartsWith("/download.html#", item.GetProperty("downloadUrl").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_customer_with_no_purchases_gets_an_empty_shelf_rather_than_an_error()
    {
        var (client, _) = await _host.SignedInCustomerAsync("lib-empty@example.com", "Empty Shelf", Password);

        Assert.Empty(await ArrayAsync(client, "/api/me/library"));
        Assert.Empty(await ArrayAsync(client, "/api/me/orders"));
    }

    [Fact]
    public async Task The_profile_stores_the_preferred_language_and_the_customer_row_follows()
    {
        const string email = "lib-profile@example.com";
        await _host.PaidGuestOrderAsync(email, "Original Name", "LIBPR", bookId: 17);
        var (client, user) = await _host.SignedInCustomerAsync(email, "Original Name", Password);

        var response = await client.PutAsync("/api/me/profile", JsonContent(new
        {
            displayName = "منى العبدالله",
            preferredLanguage = "ar"
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var profile = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("ar", profile.GetProperty("language").GetString());

        // AC-6: written where a background job can read it, because the dispatcher has
        // no session and still has to pick a language.
        using var scope = _host.Scope();
        var db = _host.Service<NexusDbContext>(scope);
        var customer = await db.Customers.AsNoTracking().FirstAsync(c => c.Id == user.CustomerId!.Value);

        Assert.Equal("ar", customer.PreferredLanguage);
        Assert.Equal("منى العبدالله", customer.Name);

        // The order keeps its own snapshot: a later name change must not rewrite an
        // invoice that has already been issued.
        var order = await db.Orders.AsNoTracking().FirstAsync(o => o.CustomerId == customer.Id);
        Assert.Equal("Original Name", order.BuyerNameSnapshot);
    }

    /// <summary>
    /// The seam this phase replaces. <c>OrderService</c> already had a signed-in branch
    /// and already ignored the request body for an authenticated buyer; until now the
    /// only implementation of "who is signed in" said nobody ever was. This proves the
    /// real one resolves, and that an authenticated request still cannot name its own
    /// buyer — the same class of bug as a client-supplied total.
    /// </summary>
    [Fact]
    public async Task A_signed_in_checkout_is_filed_against_the_account_and_not_the_request_body()
    {
        const string email = "lib-checkout@example.com";
        var (client, user) = await _host.SignedInCustomerAsync(email, "Yousef Al-Dosari", Password);

        var response = await client.PostAsync("/api/checkout", JsonContent(new
        {
            bookIds = new[] { 19 },
            name = "Somebody Else Entirely",
            email = "attacker@example.com",
            language = "en"
        }));
        response.EnsureSuccessStatusCode();

        var number = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("orderNumber").GetString();

        using var scope = _host.Scope();
        var db = _host.Service<NexusDbContext>(scope);
        var order = await db.Orders.AsNoTracking().FirstAsync(o => o.OrderNumber == number);

        Assert.Equal(user.CustomerId, order.CustomerId);
        Assert.Equal(email, order.BuyerEmailSnapshot);
        Assert.Equal("Yousef Al-Dosari", order.BuyerNameSnapshot);
    }

    private static StringContent JsonContent(object value) =>
        new(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json");

    private static async Task<JsonElement[]> ArrayAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray().ToArray();
    }

}
