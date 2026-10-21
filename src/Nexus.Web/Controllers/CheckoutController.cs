using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexus.Application.Orders;

namespace Nexus.Web.Controllers;

/// <param name="TotalFils">
/// The total as a whole number of fils. JavaScript numbers are floats, so a storefront
/// that round-trips <see cref="TotalKwd"/> can drift by a fils and trip the
/// <c>price_changed</c> check on an order that never changed. Integers do not drift.
/// </param>
/// <param name="PaymentUrl">
/// Null while Stage B is held. The storefront says so rather than pretending.
/// </param>
public sealed record CheckoutResponse(
    string OrderNumber,
    decimal TotalKwd,
    int TotalFils,
    string Currency,
    string Status,
    string? PaymentUrl);

public sealed record OrderLine(string TitleEn, string TitleAr, string AuthorEn, string AuthorAr, decimal PriceKwd);

public sealed record CheckoutResult(
    string OrderNumber,
    string Status,
    decimal TotalKwd,
    string Currency,
    string Language,
    string BuyerEmail,
    IReadOnlyList<OrderLine> Items,
    bool PaymentEnabled);

public sealed record ProblemResponse(string Code, string Message, int? BookId);

/// <summary>
/// The one public way to create an order (decision R1). Two entry points would be two
/// places a total could come from; this way there is exactly one.
/// <para>
/// The order reference is handed back in an HttpOnly cookie, not in a URL (decision R2).
/// The confirmation page therefore has no query string, which is what stops the
/// reference leaking to a third party through the <c>Referer</c> header — a stronger fix
/// than the response header, because it removes the thing that would leak.
/// </para>
/// <para>
/// Payment is not wired yet: Stage B is held until there is a captured Tap sandbox
/// delivery to verify the signature against, so this creates the Pending order and
/// returns <c>paymentUrl: null</c>.
/// </para>
/// </summary>
[ApiController]
[Route("api/checkout")]
public sealed class CheckoutController(
    IOrderService orders,
    IDataProtectionProvider protection,
    ILogger<CheckoutController> log) : ControllerBase
{
    private const string CookieName = "nexus_order";

    private IDataProtector Protector => protection.CreateProtector("Nexus.CheckoutResult.v1");

    [HttpPost]
    [EnableRateLimiting("checkout")]
    public async Task<IActionResult> Create(
        [FromBody] CheckoutRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var (result, problem) = await orders.CreateAsync(request, idempotencyKey, ct);

        if (problem is not null)
        {
            // A stale cart is a conflict, not a malformed request: nothing the buyer
            // sent was wrong, the world moved underneath them.
            var status = problem.Code == "price_changed"
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status400BadRequest;

            return StatusCode(status, new ProblemResponse(problem.Code, problem.Message, problem.BookId));
        }

        var order = result!.Order;

        if (result.AlreadyExisted)
            log.LogInformation("Checkout replayed for {OrderNumber}; returning the existing order.", order.OrderNumber);

        IssueOrderCookie(order.OrderNumber);

        var body = new CheckoutResponse(
            order.OrderNumber,
            order.TotalKwd,
            (int)decimal.Round(order.TotalKwd * 1000m, 0),
            order.Currency,
            order.Status.ToString(),
            PaymentUrl: null);

        // 200 on a replay, 201 on a fresh order: the browser can tell whether its retry
        // created something.
        return result.AlreadyExisted
            ? Ok(body)
            : CreatedAtAction(nameof(Create), new { order = order.OrderNumber }, body);
    }

    /// <summary>
    /// What the confirmation page reads. Authorised by the cookie alone — no order
    /// number, no email and no token in the URL, so there is nothing in the address bar
    /// to leak, share by accident or guess.
    /// </summary>
    [HttpGet("result")]
    public async Task<IActionResult> Result(CancellationToken ct)
    {
        if (!Request.Cookies.TryGetValue(CookieName, out var sealed_) || string.IsNullOrEmpty(sealed_))
            return NotFound();

        string orderNumber;
        try { orderNumber = Protector.Unprotect(sealed_); }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Tampered, or protected with a key ring this server no longer has.
            Response.Cookies.Delete(CookieName);
            return NotFound();
        }

        var order = await orders.GetByNumberAsync(orderNumber, ct);
        if (order is null) return NotFound();

        var items = order.Items
            .Select(i => new OrderLine(i.TitleEnSnapshot, i.TitleArSnapshot,
                                       i.AuthorEnSnapshot, i.AuthorArSnapshot, i.UnitPriceKwd))
            .ToList();

        return Ok(new CheckoutResult(
            order.OrderNumber,
            order.Status.ToString(),
            order.TotalKwd,
            order.Currency,
            order.Language,
            order.BuyerEmailSnapshot,
            items,
            PaymentEnabled: false));
    }

    private void IssueOrderCookie(string orderNumber) =>
        Response.Cookies.Append(CookieName, Protector.Protect(orderNumber), new CookieOptions
        {
            HttpOnly = true,
            // Lax rather than Strict: the buyer will return from the payment page on
            // another origin, and Strict would drop the cookie on that navigation.
            SameSite = SameSiteMode.Lax,
            Secure = Request.IsHttps,
            IsEssential = true,
            MaxAge = TimeSpan.FromHours(24)
        });
}
