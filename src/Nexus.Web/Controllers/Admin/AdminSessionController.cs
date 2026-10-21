using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexus.Application.Common;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Web.Controllers.Admin;

/// <summary>
/// Signing in and out of the control panel, on the <b>admin</b> cookie.
/// <para>
/// The demo's sign-in button was a link to the dashboard. This is the real thing: the
/// password is checked against Identity, failures count towards lockout, the request is
/// rate-limited, and the cookie that comes back is <c>nexus_admin</c> — path-scoped,
/// <c>SameSite=Strict</c>, eight hours, not sliding. A customer who signs in on the
/// storefront gets a different cookie and it opens nothing here.
/// </para>
/// </summary>
[ApiController]
[Route("admin/api/session")]
[AutoValidateAntiforgeryToken]
public sealed class AdminSessionController(
    UserManager<NexusUser> users,
    IUserClaimsPrincipalFactory<NexusUser> principals,
    IAntiforgery antiforgery,
    IAuditLog audit,
    NexusDbContext db,
    ILogger<AdminSessionController> log) : ControllerBase
{
    public sealed record SignInBody(string Email, string Password);

    /// <summary>
    /// Who is signed in, and the antiforgery token every mutating call must echo back.
    /// Anonymous: the login page needs the token before it can post.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Current()
    {
        /* The admin scheme is authenticated here, and the result assigned, BEFORE the
         * token is minted. An antiforgery token is bound to the identity it was issued
         * to, and this endpoint is anonymous — without the assignment it would hand a
         * signed-in operator a token minted for nobody, and every write they then made
         * would be refused with a bare 400 that looks exactly like a bug. */
        var result = await HttpContext.AuthenticateAsync(NexusAuth.AdminScheme);
        if (result.Succeeded && result.Principal is not null) HttpContext.User = result.Principal;

        var tokens = antiforgery.GetAndStoreTokens(HttpContext);

        if (!result.Succeeded || result.Principal is null)
            return Ok(new { authenticated = false, csrf = tokens.RequestToken });

        var roles = result.Principal.FindAll(System.Security.Claims.ClaimTypes.Role)
            .Select(c => c.Value)
            .ToArray();

        return Ok(new
        {
            authenticated = true,
            email = result.Principal.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                    ?? result.Principal.Identity?.Name,
            name = result.Principal.Identity?.Name,
            roles,
            isAdmin = roles.Contains(NexusRoles.Admin),
            csrf = tokens.RequestToken
        });
    }

    /// <remarks>
    /// Every refusal returns the same body. Telling a stranger whether an address exists,
    /// is unconfirmed, or is merely locked out turns the login form into a directory of
    /// the people who can change prices. The operator's own diagnosis is in the log.
    /// </remarks>
    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Create([FromBody] SignInBody body, CancellationToken ct)
    {
        var email = (body.Email ?? "").Trim();
        var user = string.IsNullOrEmpty(email) ? null : await users.FindByEmailAsync(email);

        var problem = await WhyNotAsync(user, body.Password);
        if (problem is not null || user is null)
        {
            log.LogWarning("Admin sign-in refused for {Email}: {Problem}", email, problem);
            return Unauthorized(new { error = "sign_in_failed" });
        }

        await users.ResetAccessFailedCountAsync(user);

        var principal = await principals.CreateAsync(user);
        await HttpContext.SignInAsync(NexusAuth.AdminScheme, principal, new AuthenticationProperties
        {
            IsPersistent = false
        });

        await audit.RecordAsync(
            "AdminSession", user.Id, "admin.signin", null,
            new { user.Email },
            user.Id, user.Email, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);

        await db.SaveChangesAsync(ct);

        var roles = await users.GetRolesAsync(user);
        return Ok(new
        {
            authenticated = true,
            email = user.Email,
            name = user.DisplayName ?? user.Email,
            roles,
            isAdmin = roles.Contains(NexusRoles.Admin)
        });
    }

    [HttpDelete]
    [Authorize(Policy = AdminPolicies.AdminOrStaff)]
    public async Task<IActionResult> Destroy(CancellationToken ct)
    {
        await audit.RecordAsync(
            "AdminSession", User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "?",
            "admin.signout", null, null, null, null, null, ct);

        await db.SaveChangesAsync(ct);
        await HttpContext.SignOutAsync(NexusAuth.AdminScheme);

        return NoContent();
    }

    /// <summary>
    /// The password check, lockout included.
    /// <para>
    /// <c>SignInManager.PasswordSignInAsync</c> is deliberately not used: it signs into
    /// <c>IdentityConstants.ApplicationScheme</c>, which this application does not
    /// register, so it would quietly mint the wrong cookie. The counting it does is
    /// reproduced here against the same <see cref="UserManager{TUser}"/>.
    /// </para>
    /// </summary>
    private async Task<string?> WhyNotAsync(NexusUser? user, string? password)
    {
        if (user is null)
        {
            // Not short-circuited: returning instantly for an unknown address makes the
            // form a timing oracle for which addresses exist.
            await Task.Delay(Random.Shared.Next(60, 140));
            return "no_such_user";
        }

        if (await users.IsLockedOutAsync(user)) return "locked_out";

        var roles = await users.GetRolesAsync(user);
        if (!roles.Contains(NexusRoles.Admin) && !roles.Contains(NexusRoles.Staff))
        {
            // A customer account must not open the panel even with the right password.
            await users.AccessFailedAsync(user);
            return "not_staff";
        }

        if (!await users.CheckPasswordAsync(user, password ?? ""))
        {
            await users.AccessFailedAsync(user);
            return "bad_password";
        }

        // RequireConfirmedEmail is on, and an unconfirmed administrator is an address
        // somebody else may still control.
        if (!await users.IsEmailConfirmedAsync(user)) return "email_unconfirmed";

        return null;
    }
}
