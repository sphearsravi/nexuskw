using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Domain;
using Nexus.Domain.Entities;
using Nexus.Infrastructure;
using Nexus.Infrastructure.Persistence;

namespace Nexus.IntegrationTests;

/// <summary>
/// The shared <see cref="NexusApp"/> with Stage C's services added and a throwaway
/// <c>App_Data</c> root.
/// <para>
/// <c>Program.cs</c> is Stage 0's file and does not call <c>AddNexusDelivery</c> yet, so
/// the tests add it the way the application will: one call, nothing else changed. When
/// that line lands in <c>Program.cs</c> this wrapper becomes a plain client and the
/// tests do not move.
/// </para>
/// </summary>
public sealed class DeliveryHost : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public DeliveryHost(NexusApp app)
    {
        LibraryRoot = Path.Combine(Path.GetTempPath(), "nexus-delivery-" + Guid.NewGuid().ToString("N")[..8]);

        _factory = app.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nexus:Library:Root"] = LibraryRoot,
                    // Short, so the saturation test does not spend half a minute
                    // proving that a wait is bounded.
                    ["Nexus:Library:StampQueueTimeoutSeconds"] = "2"
                }));

            builder.ConfigureServices(services => services.AddNexusDelivery());
        });

        Client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    public string LibraryRoot { get; }
    public HttpClient Client { get; }

    public IServiceScope Scope() => _factory.Services.CreateScope();

    public T Service<T>(IServiceScope scope) where T : notnull =>
        scope.ServiceProvider.GetRequiredService<T>();

    /// <summary>A paid single-item order with a real file behind it.</summary>
    public async Task<(Order Order, Book Book)> PaidOrderAsync(string tag, int bookId = 1)
    {
        using var scope = Scope();
        var db = Service<NexusDbContext>(scope);
        var library = Service<Nexus.Application.Delivery.IPdfLibrary>(scope);

        await library.WritePlaceholderAsync(bookId);

        var book = await db.Books.FirstAsync(b => b.Id == bookId);
        var now = DateTimeOffset.UtcNow;

        var customer = new Customer
        {
            Email = $"{tag}@example.com",
            NormalisedEmail = Customer.Normalise($"{tag}@example.com"),
            Name = "Nadia Al-Sabah",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        var order = new Order
        {
            OrderNumber = $"KW-261021-{tag.ToUpperInvariant()}"[..16],
            CustomerId = customer.Id,
            BuyerNameSnapshot = "Nadia Al-Sabah",
            BuyerEmailSnapshot = customer.Email,
            Status = OrderStatus.Paid,
            Language = "en",
            Currency = "KWD",
            SubtotalKwd = book.PriceKwd,
            VatRate = 0m,
            VatAmount = 0m,
            TotalKwd = book.PriceKwd,
            FxRate = 1m,
            TotalCharged = book.PriceKwd,
            PaymentExpiresAt = now.AddHours(24),
            PaidAt = now,
            CreatedAt = now,
            UpdatedAt = now,
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

        return (order, book);
    }

    public void Dispose()
    {
        Client.Dispose();
        _factory.Dispose();

        try
        {
            if (Directory.Exists(LibraryRoot)) Directory.Delete(LibraryRoot, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is not worth failing a test run over.
        }
    }
}
