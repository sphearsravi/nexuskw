using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Accounts;

namespace Nexus.Web.Controllers;

/// <summary>What register and forgot-password say. Identical for every address, by design.</summary>
public sealed record AcknowledgedResponse(string Status, string Delivery);

public sealed record AccountProblemResponse(string Code, string? Detail);

public sealed record ConfirmedResponse(int ClaimedOrders);

public sealed record ProfileResponse(string Email, string DisplayName, string Language);

/// <summary>
/// Registration, confirmation, sign-in and password reset for customers (AC-2).
/// <para>
/// Administrators do not pass through here. They have their own cookie, their own path
/// and their own sign-in, because a compromised customer session loses one reader's
/// books and a compromised admin session loses the catalogue (AC-8).
/// </para>
/// <para>
/// <b>No antiforgery token, and that is reasoned rather than forgotten.</b> Every write
/// here takes a JSON body, which an HTML form cannot produce and a cross-origin
/// <c>fetch</c> cannot send without a preflight this server does not answer; the customer
/// cookie is <c>SameSite=Lax</c>, so it does not travel on a cross-site POST in any case.
/// </para>
/// </summary>
[ApiController]
[Route("api/account")]
public sealed class AccountController(AccountService accounts) : ControllerBase
{
    /// <summary>
    /// 202, always, whatever the address turns out to be. A 409 for a duplicate would
    /// turn this endpoint into a membership test for any address an attacker cares about.
    /// </summary>
    [HttpPost("register")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken ct)
    {
        var (result, problem) = await accounts.RegisterAsync(request, ct);
        return problem is not null ? Refused(problem) : Acknowledged(result!);
    }

    [HttpPost("confirm")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Confirm([FromBody] ConfirmEmailRequest request, CancellationToken ct)
    {
        var (result, problem) = await accounts.ConfirmAsync(request, ct);
        return problem is not null
            ? Refused(problem)
            : Ok(new ConfirmedResponse(result!.ClaimedOrders));
    }

    [HttpPost("signin")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> SignIn([FromBody] SignInRequest request, CancellationToken ct)
    {
        var (success, problem) = await accounts.SignInAsync(request, ct);
        if (problem is not null) return Refused(problem, StatusCodes.Status401Unauthorized);

        await HttpContext.SignInAsync(NexusAuth.CustomerScheme, success!.Principal);

        return Ok(Profile(success.Customer));
    }

    /// <summary>
    /// Signs out of the customer scheme by name. Signing out of "the" session would also
    /// end an administrator's, which is a surprising thing for a storefront link to do.
    /// </summary>
    [HttpPost("signout")]
    public async Task<IActionResult> SignOutCustomer()
    {
        await HttpContext.SignOutAsync(NexusAuth.CustomerScheme);
        return NoContent();
    }

    [HttpPost("forgot")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Forgot([FromBody] ForgotPasswordRequest request, CancellationToken ct) =>
        Acknowledged(await accounts.ForgotPasswordAsync(request, ct));

    [HttpPost("reset")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Reset([FromBody] ResetPasswordRequest request, CancellationToken ct)
    {
        var (reset, problem) = await accounts.ResetPasswordAsync(request, ct);
        return reset ? NoContent() : Refused(problem!);
    }

    private static ProfileResponse Profile(SignedInCustomer customer) =>
        new(customer.Email, customer.DisplayName, customer.PreferredLanguage);

    private IActionResult Acknowledged(AcknowledgedResult result) =>
        Accepted(new AcknowledgedResponse(
            "acknowledged",
            result.Delivery == MailDelivery.Suppressed ? "suppressed" : "queued"));

    /// <summary>
    /// A code, never prose. The storefront owns every string a human reads, in both
    /// languages, and an API that invents English breaks that the first time it fires.
    /// </summary>
    private IActionResult Refused(AccountProblem problem, int status = StatusCodes.Status400BadRequest) =>
        StatusCode(status, new AccountProblemResponse(problem.Code, problem.Detail));
}
