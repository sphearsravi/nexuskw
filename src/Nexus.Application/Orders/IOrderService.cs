using Nexus.Domain.Entities;

namespace Nexus.Application.Orders;

/// <summary>What the browser sends to start a purchase. Note what is absent: a total.</summary>
/// <param name="BookIds">The cart. Duplicates are collapsed — a book is a file, not a quantity.</param>
/// <param name="Name">Buyer's name, snapshotted onto the order.</param>
/// <param name="Email">Buyer's email, snapshotted onto the order.</param>
/// <param name="Language">Interface language at checkout, snapshotted so the email matches.</param>
/// <param name="ClientTotalFils">
/// Optional, in fils: what the buyer was shown. It never decides a price — the catalogue
/// does — but if it disagrees, the order is refused with <c>price_changed</c> rather than
/// silently repriced, because charging more than the amount someone saw and agreed to is
/// not a logging matter. Omit it to make no claim and be priced from the catalogue.
/// </param>
public sealed record CheckoutRequest(
    IReadOnlyList<int> BookIds,
    string Name,
    string Email,
    string Language,
    int? ClientTotalFils = null);

/// <summary>A priced basket, computed entirely from catalogue rows.</summary>
public sealed record PricedBasket(
    IReadOnlyList<PricedLine> Lines,
    decimal SubtotalKwd,
    decimal VatRate,
    decimal VatAmount,
    decimal TotalKwd);

public sealed record PricedLine(
    int BookId,
    decimal UnitPriceKwd,
    string TitleEn,
    string TitleAr,
    string AuthorEn,
    string AuthorAr);

/// <summary>Why a basket cannot be priced, with the book that caused it.</summary>
public sealed record BasketProblem(string Code, string Message, int? BookId = null);

/// <summary>
/// Prices a basket from the catalogue. This is the only place a money total is produced,
/// so there is exactly one answer to "what does this cost" (PM-3).
/// </summary>
public interface IPricingService
{
    /// <summary>
    /// Prices the given books at their current published price. Fails if any is missing,
    /// unpublished or priced at zero — never silently drops a line, because a buyer who
    /// asked for three books and is charged for two has been wronged either way.
    /// </summary>
    Task<(PricedBasket? Basket, BasketProblem? Problem)> PriceAsync(
        IReadOnlyList<int> bookIds, CancellationToken ct = default);
}

/// <summary>Generates `KW-{yyMMdd}-{6}` order numbers. Unguessable and inside Tap's reference limit.</summary>
public interface IOrderNumberGenerator
{
    string Next(DateTimeOffset now);
}

/// <summary>
/// Who is checking out. A guest has no identity user; a signed-in customer does. The
/// implementation reads the session, never a request body.
/// </summary>
public interface ICurrentCustomer
{
    string? IdentityUserId { get; }
    bool IsAuthenticated { get; }
}

public sealed record OrderCreated(Order Order, bool AlreadyExisted);

/// <summary>
/// Creates orders. Called only from the checkout endpoint — there is one public way to
/// create an order, so there is one place a total can come from (R1).
/// </summary>
public interface IOrderService
{
    /// <summary>
    /// Creates a Pending order, or returns the existing one when
    /// <paramref name="idempotencyKey"/> has been seen before.
    /// <para>
    /// Idempotency is not optional here: a double submission on a slow connection is two
    /// orders and, in Phase 3, two charges. On a unique-violation race the implementation
    /// must branch on the constraint — idempotency key means re-read and return the
    /// existing order; order number means roll back and retry the whole unit of work with
    /// a fresh number, never SaveChanges again on a dirty context.
    /// </para>
    /// </summary>
    Task<(OrderCreated? Result, BasketProblem? Problem)> CreateAsync(
        CheckoutRequest request, string? idempotencyKey, CancellationToken ct = default);

    Task<Order?> GetByNumberAsync(string orderNumber, CancellationToken ct = default);
}
