using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexus.Infrastructure.Admin;
using Nexus.Infrastructure.Email;

namespace Nexus.Web.Controllers.Admin;

/// <summary>
/// Orders, and the two things an operator does to one: re-send the email, refund it.
/// <para>
/// Re-sending is Staff work — it is the fix for the commonest support call. Refunding is
/// Admin, because it moves money.
/// </para>
/// </summary>
[ApiController]
[Route("admin/api/orders")]
[Authorize(Policy = AdminPolicies.AdminOrStaff)]
[AutoValidateAntiforgeryToken]
public sealed class AdminOrdersController(
    OrderAdminService orders,
    IAdminEmailOperations email) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status, [FromQuery] string? q,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        Ok(await orders.ListAsync(status, q, page, pageSize, ct));

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Detail(int id, CancellationToken ct) =>
        await orders.GetAsync(id, ct) is { } detail ? Ok(detail) : NotFound(new { error = "order_not_found" });

    public sealed record ResendBody(string? Language);

    /// <summary>
    /// Re-sends the download links through the existing <c>IAdminEmailOperations</c>,
    /// which owns the rate limit, the suppression check and the dead-grant report.
    /// <para>
    /// The recipient cannot be overridden here. Changing where a download link is mailed
    /// is how a compromised staff account walks out with the catalogue, and the operator
    /// who genuinely needs it has <c>/admin/api/orders/{id}/resend-email</c>, which is
    /// Admin-gated for exactly that reason.
    /// </para>
    /// </summary>
    [HttpPost("{id:int}/resend")]
    public async Task<IActionResult> Resend(int id, [FromBody] ResendBody? body, CancellationToken ct)
    {
        var outcome = await email.ResendAsync(
            new ResendRequest(
                id,
                body?.Language,
                ToOverride: null,
                ActorId: User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
                ActorEmail: User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value,
                IpAddress: HttpContext.Connection.RemoteIpAddress?.ToString()),
            ct);

        if (outcome.Queued)
            return Accepted(new { publicRef = outcome.PublicRef, to = outcome.To, language = outcome.Language });

        return outcome.Problem switch
        {
            "order_not_found" => NotFound(new { error = outcome.Problem }),
            "invalid_language" or "invalid_recipient" => BadRequest(new { error = outcome.Problem }),
            "rate_limited" => RateLimited(outcome),
            "email_disabled" => StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = outcome.Problem }),
            "no_live_grants" => Conflict(new
            {
                error = outcome.Problem,
                books = outcome.DeadGrants?.Select(g => new { g.BookId, g.TitleEn, g.TitleAr, g.State })
            }),
            _ => Conflict(new { error = outcome.Problem ?? "resend_failed" })
        };
    }

    public sealed record RefundBody(decimal AmountKwd, string? Reason);

    /// <summary>
    /// Admin only, and currently refused.
    /// <para>
    /// The Tap integration is held pending a sandbox sample, so there is no way to move
    /// the money. The action exists, validates, writes an audit row saying it was
    /// attempted, and returns <c>503 payments_held</c>. Marking the order refunded and
    /// revoking the grants without a repayment would take a customer's books away and
    /// leave a trail asserting they had been paid back.
    /// </para>
    /// </summary>
    [HttpPost("{id:int}/refund")]
    [Authorize(Policy = AdminPolicies.AdminOnly)]
    public async Task<IActionResult> Refund(int id, [FromBody] RefundBody body, CancellationToken ct)
    {
        var problem = await orders.RefundAsync(id, body.AmountKwd, body.Reason, ct);

        return problem.Code switch
        {
            "order_not_found" => NotFound(new { error = problem.Code }),
            "order_not_paid" => Conflict(new { error = problem.Code, status = problem.Detail }),
            "refund_amount_out_of_range" => BadRequest(new { error = problem.Code, refundable = problem.Detail }),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = problem.Code })
        };
    }

    private IActionResult RateLimited(ResendOutcome outcome)
    {
        var seconds = outcome.RetryAfterSeconds ?? 3600;
        Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

        return StatusCode(StatusCodes.Status429TooManyRequests, new
        {
            error = outcome.Problem,
            scope = outcome.Scope,
            limit = outcome.Limit,
            retryAfterSeconds = seconds
        });
    }
}
