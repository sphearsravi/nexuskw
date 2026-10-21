using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Infrastructure.Admin;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Web.Controllers.Admin;

/// <summary>The dashboard figures. Read-only, and every number is counted from the database.</summary>
[ApiController]
[Route("admin/api/dashboard")]
[Authorize(Policy = AdminPolicies.AdminOrStaff)]
public sealed class AdminDashboardController(AdminDashboardService dashboard) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) => Ok(await dashboard.SummariseAsync(ct));
}

/// <summary>
/// The operator-editable settings.
/// <para>
/// Admin only. Changing the link expiry or the delivery email affects every future
/// purchase, which is a different weight of decision from re-sending one order's link.
/// </para>
/// </summary>
[ApiController]
[Route("admin/api/settings")]
[Authorize(Policy = AdminPolicies.AdminOnly)]
[AutoValidateAntiforgeryToken]
public sealed class AdminSettingsController(AdminSettingsService settings) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await settings.ListAsync(ct));

    public sealed record SettingBody(string? Value);

    /// <summary>
    /// One key at a time, by name in the route. The key must be on the editable
    /// allow-list; the same table will hold the gateway keys, and a settings endpoint that
    /// writes whatever it is handed is a way to set one from a browser.
    /// </summary>
    [HttpPut("{key}")]
    public async Task<IActionResult> Set(string key, [FromBody] SettingBody body, CancellationToken ct)
    {
        var (row, errors) = await settings.SetAsync(key, body.Value, ct);

        if (errors.Count == 0) return Ok(row);

        return errors[0].Code == "not_editable"
            ? BadRequest(new { error = "not_editable", key })
            : UnprocessableEntity(new { error = "validation_failed", fields = errors });
    }
}

/// <summary>
/// The audit trail, read back.
/// <para>
/// Admin only, and read-only: an audit log with a delete button is not one. Without a
/// screen it is a table nobody ever looks at, which is the usual way a trail turns out to
/// have been empty the whole time.
/// </para>
/// </summary>
[ApiController]
[Route("admin/api/audit")]
[Authorize(Policy = AdminPolicies.AdminOnly)]
public sealed class AdminAuditController(NexusDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? entity, [FromQuery] string? entityId,
        [FromQuery] int take = 100, CancellationToken ct = default)
    {
        var query = db.AuditLogs.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(entity)) query = query.Where(a => a.Entity == entity);
        if (!string.IsNullOrWhiteSpace(entityId)) query = query.Where(a => a.EntityId == entityId);

        var rows = await query
            .OrderByDescending(a => a.At)
            .Take(Math.Clamp(take, 1, 500))
            .Select(a => new
            {
                id = a.Id,
                entity = a.Entity,
                entityId = a.EntityId,
                action = a.Action,
                actorEmail = a.ActorEmail,
                before = a.BeforeJson,
                after = a.AfterJson,
                at = a.At,
                ip = a.IpAddress
            })
            .ToListAsync(ct);

        return Ok(rows);
    }
}
