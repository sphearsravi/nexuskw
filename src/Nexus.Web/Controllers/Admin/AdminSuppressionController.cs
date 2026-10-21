using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Domain;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Email;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Web.Controllers.Admin;

/// <summary>
/// The addresses the store must stop writing to, and the operator's ability to change its
/// mind about them.
/// <para>
/// Clearing matters as much as blocking: the commonest cause of a bounce is a mailbox that
/// was full for a week, and a permanent block over a temporary condition is how a store
/// loses a repeat buyer who never finds out why.
/// </para>
/// </summary>
[ApiController]
// Under /admin: the admin cookie is path-scoped there and is not sent to /api.
[Route("admin/api/suppressions")]
[Authorize(Policy = AdminPolicies.AdminOnly)]
[AutoValidateAntiforgeryToken]
public sealed class AdminSuppressionController(
    EmailSuppressionList suppressions,
    NexusDbContext db) : ControllerBase
{
    public sealed record SuppressBody(string Email, string? Notes);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int take = 100, CancellationToken ct = default)
    {
        var rows = await db.EmailSuppressions
            .AsNoTracking()
            .OrderByDescending(s => s.UpdatedAt)
            .Take(Math.Clamp(take, 1, 200))
            .Select(s => new
            {
                email = s.NormalisedEmail,
                reason = s.Reason.ToString(),
                classification = s.LastClassification,
                softBounces = s.SoftBounceCount,
                createdAt = s.CreatedAt,
                updatedAt = s.UpdatedAt
            })
            .ToListAsync(ct);

        return Ok(rows);
    }

    [HttpPost]
    public async Task<IActionResult> Block([FromBody] SuppressBody body, CancellationToken ct)
    {
        if (!OrderEmailService.IsPlausibleAddress(body.Email))
            return BadRequest(new { error = "invalid_recipient" });

        await suppressions.SuppressAsync(body.Email, SuppressionReason.ManualBlock, body.Notes, ct);
        await AuditAsync("email.suppression.block", body.Email, new { body.Notes }, ct);

        return Accepted(new { email = EmailSuppressionList.Normalise(body.Email) });
    }

    [HttpDelete("{email}")]
    public async Task<IActionResult> Clear(string email, CancellationToken ct)
    {
        var existing = await suppressions.FindAsync(email, ct);
        if (existing is null) return NotFound(new { error = "not_suppressed" });

        var before = new { reason = existing.Reason.ToString(), existing.LastClassification, existing.CreatedAt };
        await suppressions.ClearAsync(email, ct);

        /* The one migration gives EmailSuppression no ClearedAt/ClearedBy columns, so the
         * row is removed and the audit log carries the history instead. Why an address was
         * blocked and who unblocked it is exactly the question asked six months later, and
         * this is where the answer now lives. */
        await AuditAsync("email.suppression.clear", email, before, ct);

        return NoContent();
    }

    private async Task AuditAsync(string action, string email, object? before, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog
        {
            ActorId = User.FindFirst("sub")?.Value ?? User.Identity?.Name,
            ActorEmail = User.FindFirst("email")?.Value,
            Entity = "EmailSuppression",
            EntityId = EmailSuppressionList.Normalise(email),
            Action = action,
            BeforeJson = before is null ? null : JsonSerializer.Serialize(before),
            At = DateTimeOffset.UtcNow,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        await db.SaveChangesAsync(ct);
    }
}
