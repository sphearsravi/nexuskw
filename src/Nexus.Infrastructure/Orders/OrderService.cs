using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexus.Application.Orders;
using Nexus.Domain;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Orders;

/// <summary>
/// Creates Pending orders. A Pending order is a record of intent and nothing else: no
/// charge, no email, no download grant, no <c>SalesCount</c> change. Stage B's state
/// machine owns everything that happens after money moves.
/// </summary>
public sealed class OrderService(
    NexusDbContext db,
    IPricingService pricing,
    IOrderNumberGenerator orderNumbers,
    ICurrentCustomer currentCustomer,
    TimeProvider clock,
    OrderOptions options,
    ILogger<OrderService> log) : IOrderService
{
    public async Task<(OrderCreated? Result, BasketProblem? Problem)> CreateAsync(
        CheckoutRequest request, string? idempotencyKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var key = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
        if (key is { Length: > 64 })
        {
            return (null, new BasketProblem("invalid_request", "Idempotency-Key is longer than 64 characters."));
        }

        var (identity, identityProblem) = await ResolveIdentityAsync(request, ct);
        if (identityProblem is not null) return (null, identityProblem);

        // The replay lookup is scoped to (key, normalised email) so a guessed key can
        // never hand back another buyer's order — the key is not a secret and does not
        // authorise anything.
        if (key is not null)
        {
            var existing = await FindByKeyAsync(key, identity!.NormalisedEmail, ct);
            if (existing is not null) return (new OrderCreated(existing, AlreadyExisted: true), null);
        }

        var (basket, problem) = await pricing.PriceAsync(request.BookIds, ct);
        if (problem is not null) return (null, problem);

        var stale = ClientTotalMismatch(request.ClientTotalFils, basket!.TotalKwd);
        if (stale is not null) return (null, stale);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var order = await WriteAsync(identity!, basket, key, ct);
                return (new OrderCreated(order, AlreadyExisted: false), null);
            }
            catch (DbUpdateException ex) when (IsDuplicateKey(ex))
            {
                var violation = OrderConstraints.Classify(ex.GetBaseException().Message);

                // The unit of work is poisoned the moment SaveChanges throws: the
                // customer INSERT may already have succeeded, and saving again on a
                // dirty context turns a recoverable race into a 500. Start clean.
                db.ChangeTracker.Clear();

                if (violation == UniqueViolation.OrderIdempotencyKey && key is not null)
                {
                    var winner = await FindByKeyAsync(key, identity!.NormalisedEmail, ct);
                    if (winner is not null)
                    {
                        log.LogInformation(
                            "Idempotency-Key {Key} lost the write race; returning order {OrderNumber}.",
                            key, winner.OrderNumber);

                        return (new OrderCreated(winner, AlreadyExisted: true), null);
                    }

                    // The row that beat us is not findable under our scope, so the key
                    // belongs to another buyer. Refuse rather than retry into the same
                    // wall for ever.
                    return (null, new BasketProblem("invalid_request",
                        "Idempotency-Key is already in use by another buyer."));
                }

                if (violation is UniqueViolation.Unknown || attempt >= options.MaxCreateAttempts)
                {
                    throw;
                }

                log.LogWarning(ex,
                    "Order creation hit {Violation} on attempt {Attempt}; re-running the unit of work.",
                    violation, attempt);
            }
        }
    }

    public Task<Order?> GetByNumberAsync(string orderNumber, CancellationToken ct = default) =>
        db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber, ct);

    /// <summary>
    /// Who is buying, and under what name the order will be filed. For a signed-in
    /// buyer the request body is ignored entirely rather than merely deprioritised: an
    /// authenticated request that lets the body name the buyer is the same class of bug
    /// as a client-supplied total.
    /// </summary>
    private async Task<(BuyerIdentity? Identity, BasketProblem? Problem)> ResolveIdentityAsync(
        CheckoutRequest request, CancellationToken ct)
    {
        if (currentCustomer is { IsAuthenticated: true, IdentityUserId: { Length: > 0 } userId })
        {
            var account = await db.Customers
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.IdentityUserId == userId, ct);

            if (account is not null)
            {
                var accountLanguage = CheckoutNormalisation.Language(request.Language, out _);
                return (new BuyerIdentity(
                    account.NormalisedEmail, account.Name, account.Email, accountLanguage, userId), null);
            }

            // Signed in with no customer row yet, so the claim-on-registration path has
            // not run. Fall through and build one from the body; the account id is
            // carried onto it so the order still lands in the right library.
            log.LogWarning("Identity user {UserId} has no Customer row; creating one from the request.", userId);
        }

        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length < options.MinNameLength || name.Length > options.MaxNameLength)
        {
            return (null, new BasketProblem("invalid_name",
                $"Name must be {options.MinNameLength}-{options.MaxNameLength} characters."));
        }

        if (!CheckoutNormalisation.IsPlausibleEmail(request.Email, options.MaxEmailLength))
        {
            return (null, new BasketProblem("invalid_email", "Email is not a usable address."));
        }

        var email = request.Email.Trim();
        var language = CheckoutNormalisation.Language(request.Language, out var recognised);
        if (!recognised)
        {
            log.LogWarning("Unrecognised checkout language {Language}; falling back to {Fallback}.",
                request.Language, language);
        }

        return (new BuyerIdentity(
            Customer.Normalise(email), name, email, language, currentCustomer.IdentityUserId), null);
    }

    private Task<Order?> FindByKeyAsync(string key, string normalisedEmail, CancellationToken ct) =>
        db.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(
                o => o.IdempotencyKey == key && o.Customer!.NormalisedEmail == normalisedEmail, ct);

    /// <summary>
    /// The whole unit of work, in one <c>SaveChangesAsync</c>. The customer insert, the
    /// order and its items reach the database in a single implicit transaction, so
    /// there is no half-written order to clean up and no explicit transaction to
    /// reconcile with the connection's retrying execution strategy.
    /// </summary>
    private async Task<Order> WriteAsync(
        BuyerIdentity identity, PricedBasket basket, string? key, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var customer = await ResolveCustomerAsync(identity, now, ct);

        var kwd = Currency.Kwd;
        var order = new Order
        {
            OrderNumber = orderNumbers.Next(now),
            Customer = customer,

            // Snapshotted, because Customer is a mutable convenience row that an
            // unauthenticated guest checkout can create. The watermark, the invoice and
            // every email read these two fields and never the customer (B1).
            BuyerNameSnapshot = identity.Name,
            BuyerEmailSnapshot = identity.Email,

            Status = OrderStatus.Pending,
            Language = identity.Language,
            Currency = kwd.Code,

            SubtotalKwd = basket.SubtotalKwd,
            VatRate = basket.VatRate,
            VatAmount = basket.VatAmount,
            TotalKwd = basket.TotalKwd,
            FxRate = BasketPricing.FxRate,
            TotalCharged = kwd.Round(basket.TotalKwd * BasketPricing.FxRate),

            IdempotencyKey = key,
            PaymentExpiresAt = now.AddHours(options.AbandonAfterHours),
            CreatedAt = now,
            UpdatedAt = now
        };

        foreach (var line in basket.Lines)
        {
            order.Items.Add(new OrderItem
            {
                BookId = line.BookId,
                UnitPriceKwd = line.UnitPriceKwd,
                TitleEnSnapshot = line.TitleEn,
                TitleArSnapshot = line.TitleAr,
                AuthorEnSnapshot = line.AuthorEn,
                AuthorArSnapshot = line.AuthorAr
            });
        }

        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);

        return order;
    }

    /// <summary>
    /// Finds the customer by normalised email, or creates one.
    /// <para>
    /// <b>A matched row is reused exactly as it is.</b> No unauthenticated request
    /// mutates any customer row — otherwise a stranger who knows an email could
    /// retroactively change the name on that buyer's past invoices and on the PDFs they
    /// have already downloaded. The outcome is identical whether the row existed or
    /// not, so the endpoint is not an account-enumeration oracle either (B1).
    /// </para>
    /// </summary>
    private async Task<Customer> ResolveCustomerAsync(
        BuyerIdentity identity, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await db.Customers
            .FirstOrDefaultAsync(c => c.NormalisedEmail == identity.NormalisedEmail, ct);

        if (existing is not null) return existing;

        return new Customer
        {
            Email = identity.Email,
            NormalisedEmail = identity.NormalisedEmail,
            Name = identity.Name,

            // Set server-side, never from the request: v1 sells to Kuwait only.
            Country = "KW",
            PreferredLanguage = identity.Language,
            IdentityUserId = identity.IdentityUserId,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    /// <summary>
    /// The browser's figure never decides a price — the catalogue does. But when the two
    /// disagree the buyer is looking at a cart that no longer reflects what they would be
    /// charged, so the order is refused rather than silently repriced.
    /// <para>
    /// Settled 2026-10-21. The plan called for a 409 here and the interface's own doc
    /// comment called for proceed-and-log; they are incompatible, not a matter of
    /// emphasis. 409 wins: charging more than the amount a buyer saw and agreed to is
    /// not a logging matter. The storefront re-renders the cart at the new price and
    /// lets them decide.
    /// </para>
    /// <para>
    /// The field stays optional. A request that omits it is making no claim about the
    /// price and is priced from the catalogue as before.
    /// </para>
    /// </summary>
    private BasketProblem? ClientTotalMismatch(int? clientTotalFils, decimal serverTotalKwd)
    {
        if (clientTotalFils is not { } client) return null;

        var server = BasketPricing.ToFils(serverTotalKwd);
        if (client == server) return null;

        log.LogInformation(
            "Checkout refused: client total {ClientFils} fils disagrees with the catalogue " +
            "total {ServerFils} fils. The cart is stale.", client, server);

        return new BasketProblem(
            "price_changed",
            $"The price has changed since your cart was shown. The total is now {Currency.Kwd.Format(serverTotalKwd)}.");
    }

    private static bool IsDuplicateKey(DbUpdateException ex) =>
        ex.GetBaseException() is SqlException sql && OrderConstraints.IsDuplicateKey(sql.Number);

    private sealed record BuyerIdentity(
        string NormalisedEmail, string Name, string Email, string Language, string? IdentityUserId);
}
