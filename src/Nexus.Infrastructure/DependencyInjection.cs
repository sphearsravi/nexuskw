using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Application.Catalogue;
using Nexus.Infrastructure.Catalogue;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddNexusInfrastructure(
        this IServiceCollection services, IConfiguration config)
    {
        var connection = config.GetConnectionString("Nexus")
            ?? throw new InvalidOperationException(
                "No 'Nexus' connection string. Set it in appsettings.Development.json " +
                "locally, or as an App Pool environment variable on the server.");

        services.AddDbContext<NexusDbContext>(o => o.UseSqlServer(connection, sql =>
            sql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null)));

        services.AddScoped<ICatalogueService, CatalogueService>();
        services.AddScoped<CatalogueSeeder>();

        return services;
    }
}
