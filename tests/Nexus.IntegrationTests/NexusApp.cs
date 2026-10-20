using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Infrastructure.Persistence;

namespace Nexus.IntegrationTests;

/// <summary>
/// Hosts the real application over a real SQL Server database, because the
/// things worth testing here — the publish filter, Arabic folding, decimal
/// precision — are exactly the things an in-memory provider gets wrong.
/// <para>
/// Connection comes from NEXUS_TEST_DB when set (CI's service container),
/// otherwise the local SQL Express instance.
/// </para>
/// </summary>
public sealed class NexusApp : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string LocalDefault =
        @"Server=(local)\SQLEXPRESS;Database=NexusTest;Trusted_Connection=True;TrustServerCertificate=True";

    private static string Connection =>
        Environment.GetEnvironmentVariable("NEXUS_TEST_DB") is { Length: > 0 } s ? s : LocalDefault;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development"); // no HTTPS redirect in-process

        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Nexus"] = Connection,
                // The fixture owns the schema and the seed, so startup does not.
                ["Nexus:MigrateOnStartup"] = "false"
            }));
    }

    public async Task InitializeAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();

        // A clean database every run: a test that passes only because of a row
        // left behind by the last run is worse than no test.
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();

        await scope.ServiceProvider.GetRequiredService<CatalogueSeeder>()
            .SeedAsync(publishBooks: true);
    }

    public new async Task DisposeAsync()
    {
        await Task.CompletedTask;
        base.Dispose();
    }
}
