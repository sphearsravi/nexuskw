using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Accounts;

namespace Nexus.Web.Controllers;

/// <summary>
/// The signed-in customer's own things: their library, their orders, their profile.
/// <para>
/// <b>Every route here is scoped by the session and by nothing else.</b> There is no
/// <c>/api/customers/{id}</c> and no customer id in any URL — the id comes from the
/// cookie, so a route that could return somebody else's order does not exist to be found.
/// The grant id on the re-issue route is the one exception, and it is re-checked against
/// the signed-in customer before anything happens to it.
/// </para>
/// </summary>
[ApiController]
[Route("api/me")]
[Authorize(Policy = NexusAuth.CustomerOnly)]
public sealed class MeController(
    UserManager<NexusUser> users,
    AccountService accounts,
    LibraryService library) : ControllerBase
{
    /// <summary>
    /// Also the storefront's "am I signed in" probe: a 401 here is the normal signed-out
    /// answer, which is why it returns the display shape and not the internal one. The
    /// Identity user id and the customer id stay on the server; no page needs them, and
    /// a value a page never uses is a value that cannot leak from one.
    /// </summary>
    [HttpGet("profile")]
    public async Task<IActionResult> Profile()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Unauthorized();

        NoStore();

        var profile = await accounts.ProfileAsync(user.Id);
        return profile is null ? Unauthorized() : Ok(Shown(profile));
    }

    [HttpPut("profile")]
    public async Task<IActionResult> UpdateProfile([FromBody] ProfileUpdate update, CancellationToken ct)
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var (profile, problem) = await accounts.UpdateProfileAsync(user.Id, update, ct);

        return problem is not null
            ? BadRequest(new AccountProblemResponse(problem.Code, problem.Detail))
            : Ok(Shown(profile!));
    }

    private static ProfileResponse Shown(SignedInCustomer customer) =>
        new(customer.Email, customer.DisplayName, customer.PreferredLanguage);

    /// <summary>AC-3. Newest purchase first, with each item's live counter and expiry.</summary>
    [HttpGet("library")]
    public async Task<IActionResult> Library(CancellationToken ct)
    {
        NoStore();

        // No customer row means no purchases, which is an empty shelf and not an error.
        // It is the state a confirmed account is in for the moment before it buys.
        var customerId = await CustomerIdAsync();
        return customerId is null ? Ok(Array.Empty<LibraryItem>()) : Ok(await library.LibraryAsync(customerId.Value, ct));
    }

    /// <summary>AC-4: the customer fixes the commonest complaint themselves.</summary>
    [HttpPost("library/{grantId:int}/reissue")]
    public async Task<IActionResult> ReIssue(int grantId, CancellationToken ct)
    {
        NoStore();

        var customerId = await CustomerIdAsync();
        if (customerId is null) return NotFound(new { error = "not_found" });

        var (item, problem) = await library.ReIssueAsync(customerId.Value, grantId, ct);

        if (problem is null) return Ok(item);

        // not_found covers both "no such grant" and "not yours": telling them apart
        // would confirm that another customer owns a particular grant.
        return problem == "not_found"
            ? NotFound(new { error = problem })
            : Conflict(new { error = problem });
    }

    /// <summary>AC-5, the list half.</summary>
    [HttpGet("orders")]
    public async Task<IActionResult> Orders(CancellationToken ct)
    {
        NoStore();

        var customerId = await CustomerIdAsync();
        return customerId is null
            ? Ok(Array.Empty<OrderSummary>())
            : Ok(await library.OrdersAsync(customerId.Value, ct));
    }

    /// <summary>AC-5, the invoice. KWD at three decimals, from the order's own snapshots.</summary>
    [HttpGet("orders/{orderNumber}")]
    public async Task<IActionResult> Invoice(string orderNumber, CancellationToken ct)
    {
        NoStore();

        var customerId = await CustomerIdAsync();
        if (customerId is null) return NotFound(new { error = "not_found" });

        var invoice = await library.InvoiceAsync(customerId.Value, orderNumber, ct);

        // 404 and not 403, for the same reason as above: an order number that belongs to
        // somebody else must look exactly like one that does not exist.
        return invoice is null ? NotFound(new { error = "not_found" }) : Ok(invoice);
    }

    private async Task<int?> CustomerIdAsync() => (await users.GetUserAsync(User))?.CustomerId;

    /// <summary>A download token and an invoice are not things to leave in a shared cache.</summary>
    private void NoStore()
    {
        Response.Headers.CacheControl = "no-store, private";
        Response.Headers.Pragma = "no-cache";
    }
}
