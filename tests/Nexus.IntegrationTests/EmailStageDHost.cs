using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Nexus.Application.Common;
using Nexus.Application.Delivery;
using Nexus.Application.Email;
using Nexus.Domain;
using Nexus.Domain.Entities;
using Nexus.Infrastructure;
using Nexus.Infrastructure.Email;
using Nexus.Infrastructure.Persistence;

namespace Nexus.IntegrationTests;

/// <summary>
/// Stage D's services over the real test database.
/// <para>
/// A container of its own rather than the web host, because <c>Program.cs</c> is Stage 0's
/// file and does not yet call <c>AddNexusEmail</c>. This exercises the real registration
/// extension against real SQL Server — the claim statement, the unique index and the
/// three-decimal money are exactly the things an in-memory provider gets wrong — and swaps
/// in the two contracts sibling stages own.
/// </para>
/// </summary>
internal sealed class EmailStageDHost : IDisposable
{
    private readonly ServiceProvider _services;

    public EmailStageDHost(Action<Dictionary<string, string?>>? configure = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Email:Transport"] = "FileDrop",
            ["Email:FileDrop:Path"] = Path.Combine(Path.GetTempPath(), "nexus-stage-d", Guid.NewGuid().ToString("N")),
            ["Email:BounceDomain"] = "nexus.example",
            ["Email:LeaseMinutes"] = "2",
            ["Email:BatchSize"] = "10",
            ["Site:PublicBaseUrl"] = "https://nexus.example"
        };

        configure?.Invoke(settings);

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(config);
        services.AddDbContext<NexusDbContext>(o => o.UseSqlServer(Connection));

        services.AddSingleton(Clock);
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton<ISettingsReader>(Settings);
        services.AddSingleton<IDeliveryService>(Delivery);

        services.AddNexusEmail(config);

        // The hosted dispatcher is registered by AddNexusEmail; the tests drive OutboxPump
        // directly so the loop's timing never decides whether a test passes.
        services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();
        services.AddSingleton<IEmailSender>(Sender);

        _services = services.BuildServiceProvider();
    }

    public static string Connection =>
        Environment.GetEnvironmentVariable("NEXUS_TEST_DB") is { Length: > 0 } s ? s : NexusApp.LocalDefault;

    public StageDClock Clock { get; } = new(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero));
    public StageDSettings Settings { get; } = new();
    public StageDDelivery Delivery { get; } = new();
    public StageDRecordingSender Sender { get; } = new();

    public IServiceScope Scope() => _services.CreateScope();

    public T Resolve<T>(IServiceScope scope) where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    /// <summary>Runs <paramref name="work"/> in its own scope, as a web request or a dispatcher pass would.</summary>
    public async Task<TResult> InScopeAsync<TResult>(Func<IServiceProvider, Task<TResult>> work)
    {
        using var scope = _services.CreateScope();
        return await work(scope.ServiceProvider);
    }

    public async Task InScopeAsync(Func<IServiceProvider, Task> work)
    {
        using var scope = _services.CreateScope();
        await work(scope.ServiceProvider);
    }

    /// <summary>
    /// Clears the tables this stage writes.
    /// <para>
    /// The collection fixture gives every test class one database, and a dispatcher pass
    /// claims whatever is queued — including rows an earlier test left behind. Starting
    /// from an empty outbox is the difference between a suite that tests the dispatcher
    /// and one that tests the order its classes happened to run in.
    /// </para>
    /// </summary>
    public async Task<EmailStageDHost> ResetAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();

        await db.OutboxEmails.ExecuteDeleteAsync();
        await db.EmailSuppressions.ExecuteDeleteAsync();
        await db.AuditLogs.ExecuteDeleteAsync();

        return this;
    }

    public void Dispose() => _services.Dispose();
}

/// <summary>Builds the order, the items and the grants a paid order would have.</summary>
internal static class EmailStageDData
{
    /// <summary>A paid order with one item and one active grant whose token the fake can recover.</summary>
    public static async Task<(Order Order, DownloadGrant Grant, string Token)> PaidOrderAsync(
        EmailStageDHost host, string language = "en", string? email = null, int bookId = 1,
        OrderStatus status = OrderStatus.Paid, bool withGrant = true, int maxDownloads = 3, int used = 0,
        GrantState grantState = GrantState.Active, DateTimeOffset? expiresAt = null)
    {
        var now = host.Clock.GetUtcNow();
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var address = email ?? $"buyer-{tag.ToLowerInvariant()}@example.com";

        using var scope = host.Scope();
        var db = host.Resolve<NexusDbContext>(scope);

        // A second order for the same address reuses the row, exactly as the real customer
        // upsert does — the unique index on NormalisedEmail is not negotiable.
        var normalised = Customer.Normalise(address);
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.NormalisedEmail == normalised);

        if (customer is null)
        {
            customer = new Customer
            {
                Email = address,
                NormalisedEmail = normalised,
                Name = language == "ar" ? "نورة العلي" : "Noura Al-Ali",
                PreferredLanguage = language,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
        }

        var book = await db.Books.FirstAsync(b => b.Id == bookId);

        var order = new Order
        {
            OrderNumber = $"KW-261021-{tag[..6]}",
            CustomerId = customer.Id,
            BuyerNameSnapshot = customer.Name,
            BuyerEmailSnapshot = address,
            Status = status,
            Language = language,
            Currency = "KWD",
            SubtotalKwd = book.PriceKwd,
            VatRate = 0m,
            VatAmount = 0m,
            TotalKwd = book.PriceKwd,
            FxRate = 1m,
            TotalCharged = book.PriceKwd,
            PaymentExpiresAt = now.AddHours(24),
            CreatedAt = now,
            UpdatedAt = now,
            PaidAt = status == OrderStatus.Paid ? now : null,
            Items =
            [
                new OrderItem
                {
                    BookId = book.Id,
                    UnitPriceKwd = book.PriceKwd,
                    TitleEnSnapshot = book.TitleEn,
                    TitleArSnapshot = book.TitleAr,
                    AuthorEnSnapshot = book.AuthorEn,
                    AuthorArSnapshot = book.AuthorAr
                }
            ]
        };

        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var token = "TKN" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        var grant = new DownloadGrant
        {
            OrderId = order.Id,
            BookId = book.Id,
            Sequence = 1,
            TokenHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant(),
            MaxDownloads = maxDownloads,
            UsedCount = used,
            ExpiresAt = expiresAt ?? now.AddDays(7),
            State = grantState,
            CreatedAt = now
        };

        if (withGrant)
        {
            db.DownloadGrants.Add(grant);
            await db.SaveChangesAsync();
            host.Delivery.Tokens[grant.Id] = token;
        }

        return (order, grant, token);
    }
}
