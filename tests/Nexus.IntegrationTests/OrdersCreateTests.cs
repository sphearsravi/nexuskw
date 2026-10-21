using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Application.Orders;
using Nexus.Domain;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Orders;
using Nexus.Infrastructure.Persistence;

namespace Nexus.IntegrationTests;

/// <summary>
/// Order creation against real SQL Server, because the things worth proving here —
/// three-decimal money, unique indexes, and what two requests do when they collide —
/// are exactly what an in-memory provider gets wrong.
/// </summary>
[Collection(NexusCollection.Name)]
public sealed class OrdersCreateTests(NexusApp app) : IAsyncLifetime
{
    /// <summary>Ids well clear of the twenty seeded titles, so nothing else sees them.</summary>
    private const int UnpublishedBookId = 9001;

    private const int FreeBookId = 9002;

    private static readonly Regex NumberShape =
        new("^KW-[0-9]{6}-[0123456789ABCDEFGHJKMNPQRSTVWXYZ]{6}$", RegexOptions.Compiled);

    public async Task InitializeAsync()
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();

        db.Books.AddRange(
            Fixture(UnpublishedBookId, "Withdrawn", 6.500m, published: false),

            // A published title priced at zero cannot be produced through the publish
            // gate (D7), which is the reason the pricing engine still refuses it.
            Fixture(FreeBookId, "Free", 0m, published: true));

        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();

        // The zero-priced row is published, so leaving it behind would change the
        // catalogue count another test class asserts on.
        await db.Books.Where(b => b.Id == UnpublishedBookId || b.Id == FreeBookId)
            .ExecuteDeleteAsync();
    }

    private static Book Fixture(int id, string tag, decimal price, bool published) => new()
    {
        Id = id,
        CategorySlug = "investing",
        PriceKwd = price,
        Pages = 100,
        Year = 2025,
        AddedToStoreAt = DateTimeOffset.UtcNow,
        TitleEn = $"{tag} title",
        AuthorEn = $"{tag} author",
        DescriptionEn = $"{tag} description",
        TitleAr = $"عنوان {tag}",
        AuthorAr = $"مؤلف {tag}",
        DescriptionAr = $"وصف {tag}",
        IsPublished = published,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static CheckoutRequest Request(
        IReadOnlyList<int> books, string email, string name = "Fatima Al-Sayegh",
        string language = "en", int? clientTotalFils = null) =>
        new(books, name, email, language, clientTotalFils);

    private NexusDbContext Db(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<NexusDbContext>();

    private static async Task<(OrderCreated? Result, BasketProblem? Problem)> CreateAsync(
        ServiceProvider provider, CheckoutRequest request, string? key)
    {
        using var scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IOrderService>()
            .CreateAsync(request, key);
    }

    private async Task<decimal> CatalogueTotalAsync(params int[] bookIds)
    {
        using var scope = app.Services.CreateScope();
        return await Db(scope).Books.Where(b => bookIds.Contains(b.Id)).SumAsync(b => b.PriceKwd);
    }

    // ---- money -------------------------------------------------------------

    [Fact]
    public async Task A_total_is_computed_from_the_catalogue_at_three_decimals()
    {
        using var provider = OrdersTestHost.Build(app);
        var expected = await CatalogueTotalAsync(1, 2, 3);

        var (result, problem) = await CreateAsync(
            provider, Request([1, 2, 3], "total@example.com"), Guid.NewGuid().ToString());

        Assert.Null(problem);
        Assert.NotNull(result);

        using var scope = app.Services.CreateScope();
        var stored = await Db(scope).Orders.AsNoTracking().Include(o => o.Items)
            .FirstAsync(o => o.Id == result!.Order.Id);

        Assert.Equal(expected, stored.TotalKwd);
        Assert.Equal(expected, stored.SubtotalKwd);
        Assert.Equal(expected, stored.TotalCharged);
        Assert.Equal(0m, stored.VatRate);
        Assert.Equal(0m, stored.VatAmount);
        Assert.Equal(1m, stored.FxRate);
        Assert.Equal("KWD", stored.Currency);

        // Rule 1: what Tap hashes is the three-decimal rendering of this figure.
        Assert.Equal(Currency.Kwd.Format(expected), Currency.Kwd.Format(stored.TotalCharged));
        Assert.Matches(@"^\d+\.\d{3}$", Currency.Kwd.Format(stored.TotalCharged));
    }

    // PM-3: clientTotalFils is advisory. It is never stored and never charged.
    [Fact]
    public async Task A_tampered_client_total_is_ignored()
    {
        using var provider = OrdersTestHost.Build(app);
        var expected = await CatalogueTotalAsync(4, 5);

        var (result, problem) = await CreateAsync(
            provider,
            Request([4, 5], "tamper@example.com", clientTotalFils: 1),
            Guid.NewGuid().ToString());

        Assert.Null(problem);
        Assert.Equal(expected, result!.Order.TotalKwd);
        Assert.Equal(expected, result.Order.TotalCharged);
        Assert.NotEqual(0.001m, result.Order.TotalKwd);
    }

    [Fact]
    public async Task A_matching_client_total_changes_nothing()
    {
        using var provider = OrdersTestHost.Build(app);
        var expected = await CatalogueTotalAsync(6);

        var (result, problem) = await CreateAsync(
            provider,
            Request([6], "agree@example.com", clientTotalFils: BasketPricing.ToFils(expected)),
            Guid.NewGuid().ToString());

        Assert.Null(problem);
        Assert.Equal(expected, result!.Order.TotalKwd);
    }

    // ---- what a basket may contain -----------------------------------------

    [Fact]
    public async Task An_empty_cart_is_refused()
    {
        using var provider = OrdersTestHost.Build(app);

        var (result, problem) = await CreateAsync(
            provider, Request([], "empty@example.com"), Guid.NewGuid().ToString());

        Assert.Null(result);
        Assert.Equal("cart_empty", problem!.Code);
    }

    [Fact]
    public async Task A_duplicated_id_is_collapsed_rather_than_charged_twice()
    {
        using var provider = OrdersTestHost.Build(app);
        var expected = await CatalogueTotalAsync(8);

        var (result, problem) = await CreateAsync(
            provider, Request([8, 8, 8], "dupe@example.com"), Guid.NewGuid().ToString());

        Assert.Null(problem);
        Assert.Single(result!.Order.Items);
        Assert.Equal(expected, result.Order.TotalKwd);
    }

    [Fact]
    public async Task A_missing_book_is_refused()
    {
        using var provider = OrdersTestHost.Build(app);

        var (result, problem) = await CreateAsync(
            provider, Request([1, 999_999], "missing@example.com"), Guid.NewGuid().ToString());

        Assert.Null(result);
        Assert.Equal("items_unavailable", problem!.Code);
        Assert.Equal(999_999, problem.BookId);
    }

    // Unknown and unpublished give the same answer: the order path must not become the
    // oracle the catalogue refuses to be (CAT-20).
    [Fact]
    public async Task An_unpublished_book_is_refused_and_looks_identical_to_a_missing_one()
    {
        using var provider = OrdersTestHost.Build(app);

        var (result, problem) = await CreateAsync(
            provider, Request([UnpublishedBookId], "unpub@example.com"), Guid.NewGuid().ToString());

        Assert.Null(result);
        Assert.Equal("items_unavailable", problem!.Code);
        Assert.Equal(UnpublishedBookId, problem.BookId);
    }

    [Fact]
    public async Task A_book_priced_at_zero_is_refused()
    {
        using var provider = OrdersTestHost.Build(app);

        var (result, problem) = await CreateAsync(
            provider, Request([1, FreeBookId], "free@example.com"), Guid.NewGuid().ToString());

        Assert.Null(result);
        Assert.Equal("invalid_price", problem!.Code);
        Assert.Equal(FreeBookId, problem.BookId);
    }

    [Fact]
    public async Task A_cart_over_the_cap_is_refused()
    {
        using var provider = OrdersTestHost.Build(app, after: s =>
            s.AddSingleton(new OrderOptions { MaxCartItems = 2 }));

        var (result, problem) = await CreateAsync(
            provider, Request([1, 2, 3], "big@example.com"), Guid.NewGuid().ToString());

        Assert.Null(result);
        Assert.Equal("cart_too_large", problem!.Code);
    }

    [Theory]
    [InlineData("A", "invalid_name")]
    [InlineData("", "invalid_name")]
    [InlineData("   ", "invalid_name")]
    public async Task An_unusable_name_is_refused(string name, string code)
    {
        using var provider = OrdersTestHost.Build(app);

        var (result, problem) = await CreateAsync(
            provider, Request([1], "name@example.com", name), Guid.NewGuid().ToString());

        Assert.Null(result);
        Assert.Equal(code, problem!.Code);
    }

    [Fact]
    public async Task An_unusable_email_is_refused()
    {
        using var provider = OrdersTestHost.Build(app);

        var (result, problem) = await CreateAsync(
            provider, Request([1], "not-an-address"), Guid.NewGuid().ToString());

        Assert.Null(result);
        Assert.Equal("invalid_email", problem!.Code);
    }

    // ---- snapshots ---------------------------------------------------------

    // C-priced: four distinguishable values, each asserted against its own source
    // field, so a swap of TitleAr and AuthorEn is caught here rather than discovered
    // on an Arabic invoice in 2028.
    [Fact]
    public async Task Both_languages_of_title_and_author_are_snapshotted_onto_the_item()
    {
        using var provider = OrdersTestHost.Build(app);

        using var scope = app.Services.CreateScope();
        var book = await Db(scope).Books.AsNoTracking().FirstAsync(b => b.Id == 3);

        // The fixture must actually distinguish the four fields, or the test proves nothing.
        Assert.Equal(4, new[] { book.TitleEn, book.TitleAr, book.AuthorEn, book.AuthorAr }.Distinct().Count());

        var (result, _) = await CreateAsync(
            provider, Request([3], "snap@example.com"), Guid.NewGuid().ToString());

        var item = Assert.Single(result!.Order.Items);
        Assert.Equal(book.TitleEn, item.TitleEnSnapshot);
        Assert.Equal(book.TitleAr, item.TitleArSnapshot);
        Assert.Equal(book.AuthorEn, item.AuthorEnSnapshot);
        Assert.Equal(book.AuthorAr, item.AuthorArSnapshot);
        Assert.Equal(book.PriceKwd, item.UnitPriceKwd);
    }

    [Fact]
    public async Task The_buyer_name_and_email_are_snapshotted_onto_the_order()
    {
        using var provider = OrdersTestHost.Build(app);

        var (result, _) = await CreateAsync(
            provider,
            Request([1], "buyer.snap@example.com", "فاطمة الصايغ", "ar"),
            Guid.NewGuid().ToString());

        Assert.Equal("فاطمة الصايغ", result!.Order.BuyerNameSnapshot);
        Assert.Equal("buyer.snap@example.com", result.Order.BuyerEmailSnapshot);
        Assert.Equal("ar", result.Order.Language);
    }

    // A price edit after the sale must never rewrite what was charged.
    [Fact]
    public async Task A_later_price_edit_does_not_rewrite_the_item_snapshot()
    {
        using var provider = OrdersTestHost.Build(app);

        using var scope = app.Services.CreateScope();
        var db = Db(scope);
        var original = await db.Books.AsNoTracking().Where(b => b.Id == 9).Select(b => b.PriceKwd).FirstAsync();

        var (result, _) = await CreateAsync(
            provider, Request([9], "pricefix@example.com"), Guid.NewGuid().ToString());

        try
        {
            await db.Books.Where(b => b.Id == 9)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.PriceKwd, original + 1.000m));

            var stored = await db.Orders.AsNoTracking().Include(o => o.Items)
                .FirstAsync(o => o.Id == result!.Order.Id);

            Assert.Equal(original, stored.Items.Single().UnitPriceKwd);
            Assert.Equal(original, stored.TotalKwd);
        }
        finally
        {
            await db.Books.Where(b => b.Id == 9)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.PriceKwd, original));
        }
    }

    // ---- the customer upsert -----------------------------------------------

    // B1: a stranger who knows an email must not be able to retroactively rename the
    // buyer on every invoice and watermark that email already owns.
    [Fact]
    public async Task A_guest_checkout_does_not_rename_an_existing_customer()
    {
        using var provider = OrdersTestHost.Build(app);
        const string email = "incumbent@example.com";

        var (first, _) = await CreateAsync(
            provider, Request([1], email, "Nadia Al-Hamad", "ar"), Guid.NewGuid().ToString());
        Assert.NotNull(first);

        var (second, problem) = await CreateAsync(
            provider, Request([2], email, "Mallory Impostor", "en"), Guid.NewGuid().ToString());
        Assert.Null(problem);

        using var scope = app.Services.CreateScope();
        var customer = await Db(scope).Customers.AsNoTracking()
            .SingleAsync(c => c.NormalisedEmail == Customer.Normalise(email));

        Assert.Equal("Nadia Al-Hamad", customer.Name);
        Assert.Equal("ar", customer.PreferredLanguage);

        // The second order still records what that buyer typed; the snapshot is per
        // order, which is exactly why the customer row does not have to move.
        Assert.Equal("Mallory Impostor", second!.Order.BuyerNameSnapshot);
        Assert.Equal(customer.Id, second.Order.CustomerId);
    }

    [Fact]
    public async Task A_new_email_creates_exactly_one_customer_in_Kuwait()
    {
        using var provider = OrdersTestHost.Build(app);
        const string email = "newcomer@example.com";

        await CreateAsync(provider, Request([1], email, "New Comer", "ar"), Guid.NewGuid().ToString());

        using var scope = app.Services.CreateScope();
        var customer = await Db(scope).Customers.AsNoTracking()
            .SingleAsync(c => c.NormalisedEmail == Customer.Normalise(email));

        Assert.Equal("KW", customer.Country);
        Assert.Equal("ar", customer.PreferredLanguage);
        Assert.Null(customer.IdentityUserId);
    }

    [Fact]
    public async Task Email_matching_ignores_case_and_surrounding_space()
    {
        using var provider = OrdersTestHost.Build(app);

        await CreateAsync(provider, Request([1], "Case.Test@Example.com"), Guid.NewGuid().ToString());
        await CreateAsync(provider, Request([2], "  case.test@example.COM  "), Guid.NewGuid().ToString());

        using var scope = app.Services.CreateScope();
        var matches = await Db(scope).Customers.AsNoTracking()
            .CountAsync(c => c.NormalisedEmail == Customer.Normalise("case.test@example.com"));

        Assert.Equal(1, matches);
    }

    // An authenticated request that lets the body name the buyer is the same class of
    // bug as a client-supplied total.
    [Fact]
    public async Task A_signed_in_buyer_cannot_be_renamed_by_the_request_body()
    {
        const string email = "account.holder@example.com";
        const string userId = "identity-user-1";

        using (var seed = app.Services.CreateScope())
        {
            Db(seed).Customers.Add(new Customer
            {
                Email = email,
                NormalisedEmail = Customer.Normalise(email),
                Name = "Account Holder",
                PreferredLanguage = "ar",
                IdentityUserId = userId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await Db(seed).SaveChangesAsync();
        }

        using var provider = OrdersTestHost.Build(app,
            before: s => s.AddScoped<ICurrentCustomer>(_ => new OrdersTestHost.SignedIn(userId)));

        var (result, problem) = await CreateAsync(
            provider,
            Request([1], "attacker@example.com", "Mallory Impostor"),
            Guid.NewGuid().ToString());

        Assert.Null(problem);
        Assert.Equal("Account Holder", result!.Order.BuyerNameSnapshot);
        Assert.Equal(email, result.Order.BuyerEmailSnapshot);
    }

    // ---- idempotency -------------------------------------------------------

    [Fact]
    public async Task The_same_key_twice_returns_the_first_order()
    {
        using var provider = OrdersTestHost.Build(app);
        var key = Guid.NewGuid().ToString();
        var request = Request([1, 2], "replay@example.com");

        var (first, _) = await CreateAsync(provider, request, key);
        var (second, _) = await CreateAsync(provider, request, key);

        Assert.False(first!.AlreadyExisted);
        Assert.True(second!.AlreadyExisted);
        Assert.Equal(first.Order.Id, second.Order.Id);
        Assert.Equal(first.Order.OrderNumber, second.Order.OrderNumber);
        await AssertExactlyOneOrderAsync(key);
    }

    // The retry is of the first request, not a new intent: a cart that changed in
    // between does not produce a second order.
    [Fact]
    public async Task A_replay_with_a_different_cart_still_returns_the_first_order()
    {
        using var provider = OrdersTestHost.Build(app);
        var key = Guid.NewGuid().ToString();

        var (first, _) = await CreateAsync(provider, Request([1], "replay2@example.com"), key);
        var (second, _) = await CreateAsync(provider, Request([1, 2, 3], "replay2@example.com"), key);

        Assert.True(second!.AlreadyExisted);
        Assert.Equal(first!.Order.Id, second.Order.Id);
        Assert.Single(second.Order.Items);
    }

    /// <summary>
    /// B2, the write-time half: the read-then-write check cannot see a row inserted
    /// after it ran. The racing pricing decorator inserts exactly that row, so the
    /// unique-violation branch is exercised deterministically rather than hoped for.
    /// </summary>
    [Fact]
    public async Task A_key_that_lands_after_the_read_is_recovered_from_the_unique_violation()
    {
        const string email = "writerace@example.com";
        var key = Guid.NewGuid().ToString();
        var interloperNumber = string.Empty;

        using (var seed = app.Services.CreateScope())
        {
            Db(seed).Customers.Add(new Customer
            {
                Email = email,
                NormalisedEmail = Customer.Normalise(email),
                Name = "Write Race",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await Db(seed).SaveChangesAsync();
        }

        using var provider = OrdersTestHost.Build(app, after: s =>
            s.AddScoped<IPricingService>(sp => new OrdersTestHost.RacingPricingService(
                ActivatorUtilities.CreateInstance<PricingService>(sp),
                async () =>
                {
                    if (interloperNumber.Length > 0) return;
                    interloperNumber = await InsertRivalOrderAsync(email, key);
                })));

        var (result, problem) = await CreateAsync(provider, Request([1], email), key);

        Assert.Null(problem);
        Assert.True(result!.AlreadyExisted);
        Assert.Equal(interloperNumber, result.Order.OrderNumber);
        await AssertExactlyOneOrderAsync(key);
    }

    /// <summary>
    /// The same race, but the key belongs to somebody else. The replay lookup is scoped
    /// to the buyer, so a guessed key must never hand back another buyer's order.
    /// </summary>
    [Fact]
    public async Task A_key_held_by_another_buyer_is_refused_rather_than_disclosed()
    {
        var key = Guid.NewGuid().ToString();
        var victimNumber = await InsertRivalOrderAsync("victim@example.com", key);

        using var provider = OrdersTestHost.Build(app);

        var (result, problem) = await CreateAsync(provider, Request([1], "guesser@example.com"), key);

        Assert.Null(result);
        Assert.Equal("invalid_request", problem!.Code);
        Assert.DoesNotContain(victimNumber, problem.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// B2's headline case. A sequential "same key twice" test passes against a plain
    /// read-then-write, so it proves nothing about a double-tap on a slow connection.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_requests_with_one_key_produce_exactly_one_order()
    {
        using var provider = OrdersTestHost.Build(app);

        for (var round = 0; round < 5; round++)
        {
            var key = Guid.NewGuid().ToString();
            var request = Request([1, 2], $"concurrent{round}@example.com");
            var gate = new SemaphoreSlim(0);

            async Task<OrderCreated?> Attempt()
            {
                await gate.WaitAsync();
                var (result, problem) = await CreateAsync(provider, request, key);
                Assert.Null(problem);
                return result;
            }

            var left = Task.Run(Attempt);
            var right = Task.Run(Attempt);
            gate.Release(2);

            var outcomes = await Task.WhenAll(left, right);

            await AssertExactlyOneOrderAsync(key);
            Assert.Equal(outcomes[0]!.Order.Id, outcomes[1]!.Order.Id);
            Assert.Equal(1, outcomes.Count(o => o!.AlreadyExisted));
        }
    }

    // Two first-time buyers sharing an email race for the customer row; the unique
    // index decides and the loser re-reads rather than failing the purchase.
    [Fact]
    public async Task Concurrent_first_orders_on_one_email_share_a_single_customer_row()
    {
        using var provider = OrdersTestHost.Build(app);
        const string email = "shared.customer@example.com";
        var gate = new SemaphoreSlim(0);

        async Task<OrderCreated?> Attempt(int book)
        {
            await gate.WaitAsync();
            var (result, problem) = await CreateAsync(
                provider, Request([book], email), Guid.NewGuid().ToString());
            Assert.Null(problem);
            return result;
        }

        var left = Task.Run(() => Attempt(1));
        var right = Task.Run(() => Attempt(2));
        gate.Release(2);
        var outcomes = await Task.WhenAll(left, right);

        using var scope = app.Services.CreateScope();
        var customers = await Db(scope).Customers.AsNoTracking()
            .CountAsync(c => c.NormalisedEmail == Customer.Normalise(email));

        Assert.Equal(1, customers);
        Assert.Equal(outcomes[0]!.Order.CustomerId, outcomes[1]!.Order.CustomerId);
        Assert.NotEqual(outcomes[0]!.Order.Id, outcomes[1]!.Order.Id);
    }

    // ---- order numbers -----------------------------------------------------

    [Fact]
    public async Task The_order_number_is_in_the_agreed_shape_and_is_findable()
    {
        using var provider = OrdersTestHost.Build(app);

        var (result, _) = await CreateAsync(
            provider, Request([1], "number@example.com"), Guid.NewGuid().ToString());

        var number = result!.Order.OrderNumber;
        Assert.Matches(NumberShape, number);
        Assert.Equal(16, number.Length);
        Assert.True(number.Length <= 20, "The column and Tap's reference both cap at 20.");

        using var scope = provider.CreateScope();
        var found = await scope.ServiceProvider.GetRequiredService<IOrderService>()
            .GetByNumberAsync(number);

        Assert.NotNull(found);
        Assert.Equal(result.Order.Id, found!.Id);
    }

    [Fact]
    public async Task Sixteen_concurrent_orders_all_get_distinct_numbers()
    {
        using var provider = OrdersTestHost.Build(app);
        const int orders = 16;
        var gate = new SemaphoreSlim(0);

        async Task<string> Attempt(int index)
        {
            await gate.WaitAsync();
            var (result, problem) = await CreateAsync(
                provider,
                Request([1 + (index % 5)], $"contend{index}@example.com"),
                Guid.NewGuid().ToString());

            Assert.Null(problem);
            return result!.Order.OrderNumber;
        }

        var tasks = Enumerable.Range(0, orders).Select(i => Task.Run(() => Attempt(i))).ToArray();
        gate.Release(orders);
        var numbers = await Task.WhenAll(tasks);

        Assert.Equal(orders, numbers.Distinct(StringComparer.Ordinal).Count());
        Assert.All(numbers, n => Assert.Matches(NumberShape, n));
    }

    /// <summary>
    /// A collision is a 1-in-10^9 draw, so it is forced here. The recovery must re-run
    /// the whole unit of work on a clean context, not save again on a dirty one.
    /// </summary>
    [Fact]
    public async Task An_order_number_collision_is_retried_with_a_fresh_number()
    {
        var at = new DateTimeOffset(2026, 3, 4, 9, 0, 0, TimeSpan.Zero);

        using var provider = OrdersTestHost.Build(app, before: s =>
        {
            s.AddSingleton<TimeProvider>(new OrdersTestHost.FixedClock(at));
            s.AddSingleton<IRandomBytes>(new OrdersTestHost.ScriptedBytes(1, 1, 2));
        });

        var (first, firstProblem) = await CreateAsync(
            provider, Request([1], "collide.a@example.com"), Guid.NewGuid().ToString());
        var (second, secondProblem) = await CreateAsync(
            provider, Request([2], "collide.b@example.com"), Guid.NewGuid().ToString());

        Assert.Null(firstProblem);
        Assert.Null(secondProblem);
        Assert.Equal("KW-260304-111111", first!.Order.OrderNumber);
        Assert.Equal("KW-260304-222222", second!.Order.OrderNumber);

        // The retry must not have left a half-written unit of work behind.
        using var scope = app.Services.CreateScope();
        Assert.Equal(1, await Db(scope).Customers.CountAsync(
            c => c.NormalisedEmail == Customer.Normalise("collide.b@example.com")));
        Assert.Single(await Db(scope).Orders.AsNoTracking()
            .Where(o => o.OrderNumber == second.Order.OrderNumber).ToListAsync());
    }

    // ---- helpers -----------------------------------------------------------

    private async Task AssertExactlyOneOrderAsync(string key)
    {
        using var scope = app.Services.CreateScope();
        Assert.Equal(1, await Db(scope).Orders.CountAsync(o => o.IdempotencyKey == key));
    }

    /// <summary>Writes an order straight to the database, bypassing the service under test.</summary>
    private async Task<string> InsertRivalOrderAsync(string email, string key)
    {
        using var scope = app.Services.CreateScope();
        var db = Db(scope);
        var normalised = Customer.Normalise(email);

        var customer = await db.Customers.FirstOrDefaultAsync(c => c.NormalisedEmail == normalised);
        if (customer is null)
        {
            customer = new Customer
            {
                Email = email,
                NormalisedEmail = normalised,
                Name = "Rival",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.Customers.Add(customer);
        }

        var number = new OrderNumberGenerator(new CryptoRandomBytes()).Next(DateTimeOffset.UtcNow);
        db.Orders.Add(new Order
        {
            OrderNumber = number,
            Customer = customer,
            BuyerNameSnapshot = "Rival",
            BuyerEmailSnapshot = email,
            Language = "en",
            Currency = "KWD",
            SubtotalKwd = 6.500m,
            VatRate = 0m,
            VatAmount = 0m,
            TotalKwd = 6.500m,
            FxRate = 1m,
            TotalCharged = 6.500m,
            IdempotencyKey = key,
            PaymentExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();
        return number;
    }
}
