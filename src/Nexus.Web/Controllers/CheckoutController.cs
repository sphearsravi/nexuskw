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

public sealed record ProblemResponse(string Code, string Message, int? BookId);

/// <summary>
/// The one public way to create an order (decision R1). Two entry points would be two
/// places a total could come from; this way there is exactly one.
/// <para>
/// Payment is not wired yet. Stage B is held until there is a captured Tap sandbox
/// delivery to verify the signature against, so this creates the Pending order and
/// returns <c>paymentUrl: null</c>. The storefront shows that honestly rather than
/// pretending to take money.
/// </para>
/// </summary>
[ApiController]
[Route("api/checkout")]
public sealed class CheckoutController(IOrderService orders, ILogger<CheckoutController> log) : ControllerBase
{
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
}
