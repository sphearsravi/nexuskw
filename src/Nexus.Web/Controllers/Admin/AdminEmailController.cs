using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Domain;
using Nexus.Infrastructure.Email;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Web.Controllers.Admin;

/// <summary>
/// The operator's view of the outbox, and the resend that fixes the commonest support call.
/// <para>
/// Nothing here returns a rendered body, because there is none to return: the outbox stores
/// no body, so an admin endpoint cannot leak a live download link even by accident.
/// </para>
/// <para>
/// <b>These endpoints are built and tested but not yet reachable.</b> The <c>AdminOnly</c>
/// and <c>AdminOrStaff</c> policies and the admin authentication scheme are Stage 0's
/// <c>Program.cs</c>, which this stage does not edit. With no user store yet, nothing can
/// authenticate, and that is the correct and safe state.
/// </para>
/// </summary>
[ApiController]
// Under /admin, not /api/admin: the admin cookie is path-scoped to /admin, so a
// request to /api/admin never carries it and is answered 401 for ever. See
// DependencyInjection.Identity.cs and AdminCatalogueController.
[Route("admin/api")]
[Authorize(Policy = AdminPolicies.AdminOrStaff)]
[AutoValidateAntiforgeryToken]
public sealed class AdminEmailController(
    IAdminEmailOperations email,
    NexusDbContext db) : ControllerBase
{
    public sealed record ResendBody(string? Language, string? To);

    [HttpPost("orders/{orderId:int}/resend-email")]
    public async Task<IActionResult> Resend(
        int orderId, [FromBody] ResendBody? body, [FromServices] IAuthorizationService authorisation,
        CancellationToken ct)
    {
        /* Overriding the recipient is how a compromised staff account would send mail from
         * our domain to an arbitrary address, so it is the one part of this endpoint that
         * is Admin rather than Staff. The sending domain's reputation is the asset. */
        if (!string.IsNullOrWhiteSpace(body?.To))
        {
            var allowed = await authorisation.AuthorizeAsync(User, AdminPolicies.AdminOnly);
            if (!allowed.Succeeded) return Forbid();
        }

        var outcome = await email.ResendAsync(
            new ResendRequest(
                orderId,
                body?.Language,
                body?.To,
                ActorId: User.FindFirst("sub")?.Value ?? User.Identity?.Name,
                ActorEmail: User.FindFirst("email")?.Value,
                IpAddress: HttpContext.Connection.RemoteIpAddress?.ToString()),
            ct);

        if (outcome.Queued)
        {
            return Accepted(new
            {
                publicRef = outcome.PublicRef,
                status = nameof(EmailStatus.Queued),
                to = outcome.To,
                language = outcome.Language,
                queuedAt = DateTimeOffset.UtcNow
            });
        }

        return outcome.Problem switch
        {
            "order_not_found" => NotFound(new { error = outcome.Problem }),
            "invalid_language" or "invalid_recipient" => BadRequest(new { error = outcome.Problem }),
            "order_not_paid" => Conflict(new { error = outcome.Problem }),
            "recipient_suppressed" => Conflict(new { error = outcome.Problem }),
            "no_live_grants" => Conflict(new
            {
                error = outcome.Problem,
                books = outcome.DeadGrants?.Select(g => new
                {
                    bookId = g.BookId,
                    titleEn = g.TitleEn,
                    titleAr = g.TitleAr,
                    grantState = g.State
                })
            }),
            "email_disabled" => StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = outcome.Problem }),
            "rate_limited" => RateLimited(outcome),
            _ => BadRequest(new { error = outcome.Problem ?? "invalid_request" })
        };
    }

    [HttpGet("orders/{orderId:int}/emails")]
    public async Task<IActionResult> ForOrder(int orderId, CancellationToken ct)
    {
        var rows = await db.OutboxEmails
            .AsNoTracking()
            .Where(e => e.OrderId == orderId)
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => new
            {
                publicRef = e.PublicRef,
                kind = e.Kind.ToString(),
                language = e.Language,
                to = e.ToEmail,
                status = e.Status.ToString(),
                attempts = e.AttemptCount,
                subject = e.Subject,
                createdAt = e.CreatedAt,
                sentAt = e.SentAt,
                lastError = e.LastError
            })
            .ToListAsync(ct);

        return Ok(rows);
    }

    /// <summary>
    /// The operations view. Paged, unlike the catalogue: this table grows without bound
    /// where the catalogue does not.
    /// </summary>
    [HttpGet("outbox")]
    [Authorize(Policy = AdminPolicies.AdminOnly)]
    public async Task<IActionResult> Outbox(
        [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var wanted = ParseStatuses(status);

        var query = db.OutboxEmails.AsNoTracking().Where(e => wanted.Contains(e.Status));

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderBy(e => e.CreatedAt)
            .Skip(Math.Max(0, page - 1) * Math.Clamp(pageSize, 1, 100))
            .Take(Math.Clamp(pageSize, 1, 100))
            .Select(e => new
            {
                publicRef = e.PublicRef,
                kind = e.Kind.ToString(),
                language = e.Language,
                to = e.ToEmail,
                status = e.Status.ToString(),
                attempts = e.AttemptCount,
                orderId = e.OrderId,
                createdAt = e.CreatedAt,
                nextAttemptAt = e.NextAttemptAt,
                sentAt = e.SentAt,
                lastError = e.LastError
            })
            .ToListAsync(ct);

        return Ok(new { total, page, pageSize = Math.Clamp(pageSize, 1, 100), rows });
    }

    /// <summary>
    /// Back to Queued, preserving <c>AttemptCount</c>: the history of how hard we already
    /// tried is exactly what the next person needs, and rewriting it to zero hides it.
    /// </summary>
    [HttpPost("outbox/{publicRef}/retry")]
    [Authorize(Policy = AdminPolicies.AdminOnly)]
    public async Task<IActionResult> Retry(string publicRef, CancellationToken ct)
    {
        var row = await db.OutboxEmails.FirstOrDefaultAsync(e => e.PublicRef == publicRef, ct);
        if (row is null) return NotFound(new { error = "outbox_row_not_found" });

        if (row.Status is EmailStatus.Sent or EmailStatus.Sending)
            return Conflict(new { error = "not_retryable", status = row.Status.ToString() });

        var before = row.Status.ToString();
        row.Status = EmailStatus.Queued;
        row.NextAttemptAt = DateTimeOffset.UtcNow;

        db.AuditLogs.Add(new Domain.Entities.AuditLog
        {
            ActorId = User.FindFirst("sub")?.Value ?? User.Identity?.Name,
            Entity = "OutboxEmail",
            EntityId = row.PublicRef,
            Action = "email.retry",
            BeforeJson = $"{{\"status\":\"{before}\"}}",
            AfterJson = "{\"status\":\"Queued\"}",
            At = DateTimeOffset.UtcNow,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        await db.SaveChangesAsync(ct);
        return Accepted(new { publicRef = row.PublicRef, status = nameof(EmailStatus.Queued) });
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

    /// <summary>Default: the three the operator actually has to do something about.</summary>
    private static HashSet<EmailStatus> ParseStatuses(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return [EmailStatus.Failed, EmailStatus.Abandoned, EmailStatus.Queued];

        var wanted = new HashSet<EmailStatus>();
        foreach (var part in status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<EmailStatus>(part, ignoreCase: true, out var parsed)) wanted.Add(parsed);
        }

        return wanted.Count == 0 ? [EmailStatus.Failed, EmailStatus.Abandoned, EmailStatus.Queued] : wanted;
    }
}

/// <summary>
/// The policy names Stage 0's <c>Program.cs</c> registers, and Phases 6 and 7 reuse.
/// Constants rather than literals so the two sides cannot drift apart by a capital letter.
/// </summary>
public static class AdminPolicies
{
    public const string AdminOnly = "AdminOnly";
    public const string AdminOrStaff = "AdminOrStaff";
}
