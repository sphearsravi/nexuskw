using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nexus.Application.Common;
using Nexus.Application.Delivery;
using Nexus.Infrastructure.Delivery;
using Nexus.Infrastructure.Settings;

namespace Nexus.Infrastructure;

/// <summary>
/// Stage C's own registration file, so four parallel streams never edit one method.
/// <c>Program.cs</c> calls <see cref="AddNexusDelivery"/> once.
/// </summary>
public static class DeliveryRegistration
{
    /// <remarks>
    /// Two things still belong in <c>Program.cs</c> and are not registrable from here:
    /// the rate-limiter policies the download path assumes (per token 10/5 min, per IP
    /// 30/5 min on <c>/download/*</c>, with the per-IP limiter disabled rather than
    /// applied globally when the resolved address is a proxy), and the
    /// <c>ForwardedHeaders</c> configuration with explicit <c>KnownProxies</c> that makes
    /// the client address real in the first place.
    /// </remarks>
    public static IServiceCollection AddNexusDelivery(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddOptions<DeliveryOptions>()
            .Configure<IConfiguration>((o, c) => c.GetSection(DeliveryOptions.Section).Bind(o));

        // App_Data is server-owned: never web-served, never in a publish, never mirrored
        // over. Resolving the root once, here, is what keeps every other class from
        // inventing its own idea of where the product lives.
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<DeliveryOptions>>().Value;
            var root = string.IsNullOrWhiteSpace(options.Root)
                ? Path.Combine(sp.GetRequiredService<IHostEnvironment>().ContentRootPath, "App_Data")
                : options.Root;

            var paths = new LibraryPaths(root);
            paths.EnsureDirectories();
            return paths;
        });

        services.AddSingleton<PdfConcurrency>();
        services.AddSingleton(sp =>
            new StampFontResolver(sp.GetRequiredService<IOptions<DeliveryOptions>>().Value.StampFontPath));

        services.AddScoped<SettingsReader>();
        services.AddScoped<ISettingsReader>(sp => sp.GetRequiredService<SettingsReader>());
        services.AddScoped<IPublicSettingsReader>(sp => sp.GetRequiredService<SettingsReader>());
        services.AddScoped<DeliverySettingsSeeder>();

        services.AddScoped<IPdfLibrary, PdfLibrary>();
        services.AddScoped<IPdfStamper, PdfSharpStamper>();
        services.AddScoped<DeliveryService>();
        services.AddScoped<IDeliveryService>(sp => sp.GetRequiredService<DeliveryService>());
        services.AddScoped<IGrantStatusReader>(sp => sp.GetRequiredService<DeliveryService>());

        return services;
    }
}
