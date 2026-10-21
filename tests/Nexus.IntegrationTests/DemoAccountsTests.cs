using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Nexus.IntegrationTests;

/// <summary>
/// The sign-in pages offer a one-click demo fill. Nothing about it is compiled into a
/// page: the credentials come from an endpoint that exists only outside production, so
/// the button appears only where the endpoint answers.
/// <para>
/// This asserts the guard directly, in a host booted as Production, because the whole
/// design rests on that one check and a convenience that leaks into a live store is a
/// published credential.
/// </para>
/// </summary>
public sealed class DemoAccountsTests
{
    private sealed class Host(string environment) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Nexus"] = NexusApp.Connection,   // NEXUS_TEST_DB in CI
                    ["Nexus:MigrateOnStartup"] = "false",
                    // Present in configuration on purpose: the environment, not the
                    // absence of settings, must be what withholds them.
                    ["Admin:Email"] = "admin@example.test",
                    ["Admin:Password"] = "a-long-enough-admin-password",
                    ["Demo:CustomerEmail"] = "demo@example.test",
                    ["Demo:CustomerPassword"] = "a-long-enough-demo-password",
                }));
        }
    }

    [Fact]
    public async Task Production_does_not_serve_demo_credentials()
    {
        using var app = new Host("Production");
        var response = await app.CreateClient().GetAsync("/api/dev/demo-accounts");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // And nothing resembling a credential leaks through the body.
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("demo@example.test", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Staging_does_not_serve_them_either()
    {
        using var app = new Host("Staging");
        var response = await app.CreateClient().GetAsync("/api/dev/demo-accounts");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Development_serves_them_so_the_button_can_exist()
    {
        using var app = new Host("Development");
        var response = await app.CreateClient().GetAsync("/api/dev/demo-accounts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("admin@example.test", body, StringComparison.Ordinal);
        Assert.Contains("demo@example.test", body, StringComparison.Ordinal);
    }
}
