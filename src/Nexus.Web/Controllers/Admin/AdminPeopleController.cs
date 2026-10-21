using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexus.Infrastructure.Admin;

namespace Nexus.Web.Controllers.Admin;

/// <summary>Customers: who they are, what they bought, and erasing them on request.</summary>
[ApiController]
[Route("admin/api/customers")]
[Authorize(Policy = AdminPolicies.AdminOrStaff)]
[AutoValidateAntiforgeryToken]
public sealed class AdminCustomersController(CustomerAdminService customers) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        CancellationToken ct = default) =>
        Ok(await customers.ListAsync(q, page, pageSize, ct));

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Detail(int id, CancellationToken ct) =>
        await customers.GetAsync(id, ct) is { } detail
            ? Ok(detail)
            : NotFound(new { error = "customer_not_found" });

    /// <summary>
    /// Delete-my-data, Admin only and irreversible.
    /// <para>
    /// The order rows survive with their buyer details overwritten, because they are an
    /// accounting record; the grants are revoked and the download log loses its addresses.
    /// Staff cannot do this: it destroys a customer's library, and the person who takes
    /// that decision should be the person answerable for it.
    /// </para>
    /// </summary>
    [HttpDelete("{id:int}")]
    [Authorize(Policy = AdminPolicies.AdminOnly)]
    public async Task<IActionResult> Erase(int id, CancellationToken ct)
    {
        var (done, problem) = await customers.EraseAsync(id, ct);
        if (done) return NoContent();

        return problem!.Code switch
        {
            "customer_not_found" => NotFound(new { error = problem.Code }),
            _ => Conflict(new { error = problem.Code })
        };
    }
}

/// <summary>The delivery log, and the two actions that resolve a download complaint.</summary>
[ApiController]
[Route("admin/api/downloads")]
[Authorize(Policy = AdminPolicies.AdminOrStaff)]
[AutoValidateAntiforgeryToken]
public sealed class AdminDownloadsController(DownloadAdminService downloads) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? q, [FromQuery] string? state, [FromQuery] bool flagged = false,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        Ok(await downloads.ListAsync(q, state, flagged, page, pageSize, ct));

    [HttpGet("{grantId:int}/log")]
    public async Task<IActionResult> Log(int grantId, CancellationToken ct) =>
        Ok(await downloads.LogAsync(grantId, 100, ct));

    public sealed record RevokeBody(string? Reason);

    [HttpPost("{grantId:int}/revoke")]
    public async Task<IActionResult> Revoke(int grantId, [FromBody] RevokeBody? body, CancellationToken ct)
    {
        var (grant, problem) = await downloads.RevokeAsync(grantId, body?.Reason, ct);
        return problem switch
        {
            null => Ok(grant),
            { Code: "grant_not_found" } => NotFound(new { error = problem.Code }),
            _ => Conflict(new { error = problem.Code })
        };
    }

    /// <summary>
    /// Issues a fresh grant and kills the old one. The new token is not in the response:
    /// the buyer gets it by email, and an operator screen that displays a working download
    /// link is a free copy of every book for anyone who reaches that screen.
    /// </summary>
    [HttpPost("{grantId:int}/reissue")]
    public async Task<IActionResult> ReIssue(int grantId, CancellationToken ct)
    {
        var (grant, problem) = await downloads.ReIssueAsync(grantId, ct);
        return problem switch
        {
            null => Ok(grant),
            { Code: "not_found" } => NotFound(new { error = problem.Code }),
            _ => Conflict(new { error = problem.Code })
        };
    }
}
