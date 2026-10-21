using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Nexus.IntegrationTests;

/// <summary>
/// The checkout endpoint over real HTTP — the one public way to create an order (R1).
/// </summary>
[Collection(NexusCollection.Name)]
public sealed class CheckoutEndpointTests(NexusApp app)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static object Body(int[] books, string email, int? clientTotalFils = null) => new
    {
        bookIds = books,
        name = "Fatima Al-Sayegh",
        email,
        language = "en",
        clientTotalFils
    };

    private HttpClient Client() => app.CreateClient();

    private static HttpRequestMessage Post(object body, string? key)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/checkout")
        { Content = JsonContent.Create(body, options: Json) };
        if (key is not null) req.Headers.Add("Idempotency-Key", key);
        return req;
    }

    [Fact]
    public async Task A_valid_cart_creates_an_order()
    {
        var response = await Client().SendAsync(Post(Body([1, 8], "http.ok@example.com"), Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Matches(@"^KW-\d{6}-[0-9A-Z]{6}$", body.GetProperty("orderNumber").GetString()!);
        Assert.Equal("Pending", body.GetProperty("status").GetString());
        Assert.Equal("KWD", body.GetProperty("currency").GetString());

        // Fils as an integer: the storefront compares it exactly on the round trip, and
        // adds the currency symbol itself because Arabic puts it on the other side.
        Assert.Equal(9000, body.GetProperty("totalFils").GetInt32());
        Assert.Equal(9.000m, body.GetProperty("totalKwd").GetDecimal());
    }

    /// <summary>
    /// Payment is deliberately absent until there is a Tap sandbox sample to verify the
    /// signature against. The endpoint says so rather than pretending.
    /// </summary>
    [Fact]
    public async Task Payment_is_not_offered_while_stage_b_is_held()
    {
        var response = await Client().SendAsync(Post(Body([1], "http.nopay@example.com"), Guid.NewGuid().ToString()));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("paymentUrl").ValueKind);
    }

    /// <summary>A stale cart is a conflict: nothing the buyer sent was wrong.</summary>
    [Fact]
    public async Task A_stale_cart_is_409_not_400()
    {
        var response = await Client().SendAsync(
            Post(Body([1, 8], "http.stale@example.com", clientTotalFils: 1), Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("price_changed", body.GetProperty("code").GetString());
        // The message must carry the new total, or the storefront cannot explain itself.
        Assert.Matches(@"\d+\.\d{3}", body.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task An_empty_cart_is_400()
    {
        var response = await Client().SendAsync(Post(Body([], "http.empty@example.com"), Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_book_is_400()
    {
        var response = await Client().SendAsync(Post(Body([9999], "http.ghost@example.com"), Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// A retried submission returns 200 and the same order, so the browser can tell its
    /// retry created nothing new. Two orders here would mean two charges in Phase 3.
    /// </summary>
    [Fact]
    public async Task A_replayed_request_returns_200_and_the_same_order()
    {
        var key = Guid.NewGuid().ToString();
        var body = Body([2], "http.replay@example.com");

        var first = await Client().SendAsync(Post(body, key));
        var second = await Client().SendAsync(Post(body, key));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var a = await first.Content.ReadFromJsonAsync<JsonElement>(Json);
        var b = await second.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(a.GetProperty("orderNumber").GetString(), b.GetProperty("orderNumber").GetString());
    }
}
