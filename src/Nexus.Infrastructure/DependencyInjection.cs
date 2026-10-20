using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Application.Catalogue;
using Nexus.Infrastructure.Catalogue;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure;

public static class DependencyInjection
{
    /// <remarks>
    /// The connection string is resolved from the service provider, not from the
    /// <see cref="IConfiguration"/> captured here. Reading it eagerly meant a test
    /// host could not override it: <c>WebApplicationBuilder</c> has already
    /// materialised its configuration by the time this runs, so the value was
    /// baked in before <c>WebApplicationFactory</c> added its own source — and the
    /// integration tests silently ran against the development database.
    /// </remarks>
    public static IServiceCollection AddNexusInfrastructure(
        this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<NexusDbContext>((sp, o) =>
        {
            var connection = sp.GetRequiredService<IConfiguration>().GetConnectionString("Nexus")
                ?? throw new InvalidOperationException(
                    "No 'Nexus' connection string. Set it in appsettings.Development.json " +
                    "locally, or as an App Pool environment variable on the server.");

            o.UseSqlServer(connection, sql => sql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null));
        });

        services.AddScoped<ICatalogueService, CatalogueService>();
        services.AddScoped<CatalogueSeeder>();

        return services;
    }
}
