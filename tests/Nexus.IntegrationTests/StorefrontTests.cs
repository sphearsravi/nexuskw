using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;

namespace Nexus.IntegrationTests;

public sealed class NexusApp : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.UseEnvironment("Development"); // no HTTPS redirect in-process
}

public sealed class StorefrontTests(NexusApp app) : IClassFixture<NexusApp>
{
    private readonly HttpClient _client = app.CreateClient();

    [Fact]
    public async Task Health_endpoint_reports_healthy()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Root_serves_the_storefront_home_page()
    {
        var html = await _client.GetStringAsync("/");
        Assert.Contains("<title>Nexus", html, StringComparison.Ordinal);
    }

    // The storefront is the demo served verbatim. If one of these 404s, a page was
    // lost in a deploy or a rename — which is exactly the regression worth catching.
    [Theory]
    [InlineData("/shop.html")]
    [InlineData("/book.html")]
    [InlineData("/cart.html")]
    [InlineData("/checkout.html")]
    [InlineData("/library.html")]
    [InlineData("/success.html")]
    [InlineData("/assets/css/site.css")]
    [InlineData("/assets/js/i18n.js")]
    public async Task Storefront_page_is_served(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // App_Data holds the PDF catalogue and the DataProtection key ring. If any of
    // these ever returns 200 the catalogue is being given away — see PLAN.md §7.
    [Theory]
    [InlineData("/App_Data/library/")]
    [InlineData("/App_Data/dpkeys/")]
    [InlineData("/appsettings.json")]
    [InlineData("/appsettings.Development.json")]
    public async Task Server_owned_files_are_not_web_reachable(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
