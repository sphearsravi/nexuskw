namespace Nexus.IntegrationTests;

/// <summary>
/// Response headers set for every request. These are cheap to add and easy to lose in a
/// refactor, which is exactly why they are asserted rather than assumed.
/// </summary>
[Collection(NexusCollection.Name)]
public sealed class SecurityHeaderTests(NexusApp app)
{
    private readonly HttpClient _client = app.CreateClient();

    /// <summary>
    /// The one that is load-bearing rather than hygiene. The success page carries an
    /// order reference in its query string and loads a font cross-origin, so without
    /// this the reference leaks to that third party in the Referer header.
    /// <para>
    /// It is set as a server header rather than a per-page meta tag because the next
    /// page somebody adds will forget the meta tag.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("/")]
    [InlineData("/success.html")]
    [InlineData("/api/books")]
    [InlineData("/health")]
    public async Task Referrer_policy_is_no_referrer_on_every_response(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.True(response.Headers.TryGetValues("Referrer-Policy", out var values),
            $"{path} returned no Referrer-Policy header");
        Assert.Equal("no-referrer", values!.Single());
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/api/books")]
    public async Task Content_type_sniffing_is_disabled(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.True(response.Headers.TryGetValues("X-Content-Type-Options", out var values));
        Assert.Equal("nosniff", values!.Single());
    }

    [Fact]
    public async Task The_storefront_may_not_be_framed()
    {
        var response = await _client.GetAsync("/");
        Assert.True(response.Headers.TryGetValues("X-Frame-Options", out var values));
        Assert.Equal("DENY", values!.Single());
    }

    /// <summary>The health endpoint reports per-check detail, not just a bare word.</summary>
    [Fact]
    public async Task Health_reports_the_database_check()
    {
        var body = await _client.GetStringAsync("/health");
        Assert.Contains("\"database\"", body, StringComparison.Ordinal);
        Assert.Contains("Healthy", body, StringComparison.Ordinal);
    }
}
