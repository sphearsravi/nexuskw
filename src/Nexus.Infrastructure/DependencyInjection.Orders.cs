using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Application.Orders;
using Nexus.Infrastructure.Orders;

namespace Nexus.Infrastructure;

public static class OrdersDependencyInjection
{
    /// <summary>
    /// Registers the order stream: pricing, order numbers and order creation.
    /// <para>
    /// <see cref="ICurrentCustomer"/> and <see cref="TimeProvider"/> go in with
    /// <c>TryAdd</c> so Phase 6 can register a real, <c>HttpContext</c>-backed signed-in
    /// customer ahead of this call and a test can substitute a fixed clock, without
    /// either having to know this file exists.
    /// </para>
    /// </summary>
    public static IServiceCollection AddNexusOrders(
        this IServiceCollection services, Action<OrderOptions>? configure = null)
    {
        var options = new OrderOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IRandomBytes, CryptoRandomBytes>();
        services.TryAddSingleton<IOrderNumberGenerator, OrderNumberGenerator>();
        services.TryAddScoped<ICurrentCustomer, GuestCustomer>();

        services.AddScoped<IPricingService, PricingService>();
        services.AddScoped<IOrderService, OrderService>();

        return services;
    }
}
