using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nexus.Application.Delivery;
using Nexus.Domain;
using Nexus.Domain.Entities;
using Nexus.Infrastructure;
using Nexus.Infrastructure.Accounts;
using Nexus.Infrastructure.Persistence;

namespace Nexus.IntegrationTests;

/// <summary>
/// The shared <see cref="NexusApp"/> with Phase 6's services added.
/// <para>
/// <c>Program.cs</c> is Stage 0's file and does not call <c>AddNexusAccounts</c> yet, so
/// the tests add it the way the application will: one call, nothing else changed.
/// </para>
/// </summary>
public sealed class AccountsHost : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private int _clients;

    public AccountsHost(NexusApp app)
    {
        _factory = app.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.AddNexusAccounts();
                services.AddSingleton<IStartupFilter, PerTestClientAddress>();
            }));

        /* The fixture turns startup seeding off and owns the schema, so the three roles
         * do not exist yet. Registration puts a new account in Customer, and a role that
         * is missing makes every CustomerOnly route 403 for a reason that has nothing to
         * do with the code under test. */
        using var scope = Scope();
        var roles = Service<RoleManager<IdentityRole>>(scope);
        foreach (var role in NexusRoles.All)
        {
            if (!roles.RoleExistsAsync(role).GetAwaiter().GetResult())
                roles.CreateAsync(new IdentityRole(role)).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// A client with its own apparent IP address, and therefore its own rate-limit
    /// bucket. In-process every request would otherwise arrive with no remote address at
    /// all, share one bucket of eight per five minutes, and make the fourth test in a
    /// class fail for a reason that has nothing to do with it.
    /// </summary>
    public HttpClient Client(string? address = null)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            PerTestClientAddress.Header, address ?? $"203.0.113.{Interlocked.Increment(ref _clients) % 250 + 1}");
        return client;
    }

    public IServiceScope Scope() => _factory.Services.CreateScope();

    public T Service<T>(IServiceScope scope) where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    /// <summary>Registers, confirms out of band, and returns the signed-in client.</summary>
    public async Task<(HttpClient Client, NexusUser User)> SignedInCustomerAsync(
        string email, string name = "Nadia Al-Sabah", string password = "correct-horse-battery")
    {
        var client = Client();

        var registered = await client.PostAsJsonAsync("/api/account/register",
            new { name, email, password, language = "en" });
        registered.EnsureSuccessStatusCode();

        await ConfirmAsync(client, email);

        var signedIn = await client.PostAsJsonAsync("/api/account/signin", new { email, password });
        signedIn.EnsureSuccessStatusCode();

        return (client, await UserAsync(email));
    }

    /// <summary>
    /// Confirms by minting the token from <c>UserManager</c> rather than by reading a
    /// message: the default transport is None, so there is no mail to read, and the
    /// endpoint being tested is the one that takes the token either way.
    /// </summary>
    public async Task<HttpResponseMessage> ConfirmAsync(HttpClient client, string email)
    {
        using var scope = Scope();
        var users = Service<UserManager<NexusUser>>(scope);
        var user = await users.FindByEmailAsync(email) ?? throw new InvalidOperationException($"No user {email}.");
        var token = AccountTokens.Encode(await users.GenerateEmailConfirmationTokenAsync(user));

        return await client.PostAsJsonAsync("/api/account/confirm", new { userId = user.Id, token });
    }

    public async Task<NexusUser> UserAsync(string email)
    {
        using var scope = Scope();
        var users = Service<UserManager<NexusUser>>(scope);
        return await users.FindByEmailAsync(email) ?? throw new InvalidOperationException($"No user {email}.");
    }

    /// <summary>A paid guest order against <paramref name="email"/>, with its grants issued.</summary>
    public async Task<Order> PaidGuestOrderAsync(string email, string name, string tag, int bookId = 1)
    {
        using var scope = Scope();
        var db = Service<NexusDbContext>(scope);
        var library = Service<IPdfLibrary>(scope);
        var delivery = Service<IDeliveryService>(scope);

        await library.WritePlaceholderAsync(bookId);

        var book = await db.Books.FirstAsync(b => b.Id == bookId);
        var now = DateTimeOffset.UtcNow;
        var normalised = Customer.Normalise(email);

        var customer = await db.Customers.FirstOrDefaultAsync(c => c.NormalisedEmail == normalised);
        if (customer is null)
        {
            customer = new Customer
            {
                Email = email,
                NormalisedEmail = normalised,
                Name = name,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
        }

        var order = new Order
        {
            OrderNumber = $"KW-261021-{tag}"[..Math.Min(16, $"KW-261021-{tag}".Length)],
            CustomerId = customer.Id,
            BuyerNameSnapshot = name,
            BuyerEmailSnapshot = email,
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

        await delivery.IssueGrantsForOrderAsync(order.Id);

        return order;
    }

    public void Dispose() => _factory.Dispose();

    /// <summary>
    /// Puts the test's chosen address on the connection before anything in the real
    /// pipeline looks at it. A startup filter rather than <c>ConfigureServices</c>
    /// because this has to run ahead of <c>UseRateLimiter</c>, which lives in
    /// <c>Program.cs</c> and is not a file this phase edits.
    /// </summary>
    private sealed class PerTestClientAddress : IStartupFilter
    {
        public const string Header = "X-Test-Client";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (ctx, following) =>
                {
                    if (ctx.Request.Headers.TryGetValue(Header, out var value) &&
                        System.Net.IPAddress.TryParse(value.ToString(), out var parsed))
                    {
                        ctx.Connection.RemoteIpAddress = parsed;
                    }
                    await following();
                });

                next(app);
            };
    }
}
