using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Application.Orders;
using Nexus.Infrastructure;
using Nexus.Infrastructure.Orders;
using Nexus.Infrastructure.Persistence;

namespace Nexus.IntegrationTests;

/// <summary>
/// A container holding just the order stream, pointed at the fixture's database.
/// <para>
/// Stage 0 left <c>AddNexusOrders</c> unwired in <c>DependencyInjection.cs</c> and
/// Stage A may not edit that file, so these tests compose the stream themselves rather
/// than reaching into the web host. Stage A has no HTTP surface either — R1 gives order
/// creation one entry point and it belongs to Stage B — so nothing is lost by it.
/// </para>
/// </summary>
internal static class OrdersTestHost
{
    public static ServiceProvider Build(
        NexusApp app,
        Action<IServiceCollection>? before = null,
        Action<IServiceCollection>? after = null)
    {
        var connection = app.Services.GetRequiredService<IConfiguration>().GetConnectionString("Nexus")
            ?? throw new InvalidOperationException("The fixture has no Nexus connection string.");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<NexusDbContext>(o => o.UseSqlServer(connection));

        // Before: TryAdd registrations inside AddNexusOrders keep whatever is already
        // there, so a fixed clock or a scripted RNG goes in here.
        before?.Invoke(services);
        services.AddNexusOrders();

        // After: the services registered with plain Add are replaced by a later
        // registration, so a decorator goes in here.
        after?.Invoke(services);

        return services.BuildServiceProvider();
    }

    /// <summary>A clock that does not move, so an order number's date half is predictable.</summary>
    public sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// Hands out one byte value per call, repeating the last for ever. Lets a test force
    /// an order-number collision instead of waiting for a one-in-a-billion draw.
    /// </summary>
    public sealed class ScriptedBytes(params byte[] values) : IRandomBytes
    {
        private int _calls = -1;

        public void Fill(Span<byte> destination)
        {
            var index = Math.Min(Interlocked.Increment(ref _calls), values.Length - 1);
            destination.Fill(values[index]);
        }
    }

    /// <summary>
    /// Runs <paramref name="onPrice"/> between <c>CreateAsync</c>'s idempotency read and
    /// its insert, which is the only way to make the write-time race deterministic
    /// without putting a test seam in production code.
    /// </summary>
    public sealed class RacingPricingService(IPricingService inner, Func<Task> onPrice) : IPricingService
    {
        public async Task<(PricedBasket? Basket, BasketProblem? Problem)> PriceAsync(
            IReadOnlyList<int> bookIds, CancellationToken ct = default)
        {
            await onPrice();
            return await inner.PriceAsync(bookIds, ct);
        }
    }

    /// <summary>A buyer who is signed in, for the Phase 6 branch.</summary>
    public sealed class SignedIn(string identityUserId) : ICurrentCustomer
    {
        public string? IdentityUserId { get; } = identityUserId;

        public bool IsAuthenticated => true;
    }
}
