using System.Net;

namespace Nexus.IntegrationTests;

/// <summary>
/// The line between a customer session and an administrator's, checked from the outside
/// over real HTTP.
/// <para>
/// Both phases test this from inside their own host. This tests the assembled
/// application, because the thing most likely to go wrong is not either phase's logic
/// but the wiring between them — and the shape of a refusal is part of the contract,
/// not a detail.
/// </para>
/// </summary>
[Collection(NexusCollection.Name)]
public sealed class AuthBoundaryTests(NexusApp app)
{
    private HttpClient Anonymous() => app.CreateClient(new()
    {
        AllowAutoRedirect = false   // a redirect would hide the status we are asserting
    });

    // Every admin surface. A new one added without authorisation should break a test
    // here, not be discovered in production.
    public static TheoryData<string> AdminApi() => new()
    {
        "/admin/api/books",
        "/admin/api/categories",
        "/admin/api/orders",
        "/admin/api/settings",
    };

    /// <summary>
    /// An API answers with a status code. It must not answer with a 302 to an HTML
    /// login page: a caller expecting JSON would parse the page and report a confusing
    /// error instead of "you are not signed in".
    /// </summary>
    [Theory]
    [MemberData(nameof(AdminApi))]
    public async Task An_unauthenticated_admin_api_call_gets_a_status_code_not_a_redirect(string path)
    {
        var response = await Anonymous().GetAsync(path);

        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Found, response.StatusCode);
        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"{path} answered {(int)response.StatusCode}; expected 401 or 403");
    }

    /// <summary>The same must hold without an Accept header, which is how curl and a bare fetch call.</summary>
    [Theory]
    [MemberData(nameof(AdminApi))]
    public async Task The_refusal_does_not_depend_on_asking_for_json(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Clear();

        var response = await Anonymous().SendAsync(request);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"{path} answered {(int)response.StatusCode} with no Accept header; expected 401 or 403");
    }

    [Theory]
    [InlineData("/api/me/profile")]
    [InlineData("/api/me/library")]
    [InlineData("/api/me/orders")]
    public async Task Customer_endpoints_refuse_an_anonymous_caller(string path)
    {
        var response = await Anonymous().GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The public storefront stays public. A regression that put the catalogue behind a
    /// login would be caught here rather than by a customer.
    /// </summary>
    [Theory]
    [InlineData("/api/books")]
    [InlineData("/api/categories")]
    [InlineData("/api/settings/public")]
    public async Task The_shop_window_stays_open(string path)
    {
        var response = await Anonymous().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
