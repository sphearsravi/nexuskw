using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Nexus.Application.Orders;
using Nexus.Domain.Entities;

namespace Nexus.Infrastructure.Accounts;

/// <summary>
/// Who is signed in, read from the customer cookie and from nothing else.
/// <para>
/// This replaces <see cref="Nexus.Infrastructure.Orders.GuestCustomer"/>, which was the
/// stand-in while there was no Identity store. <c>OrderService</c> already has the
/// signed-in branch and already ignores the request body for an authenticated buyer; it
/// needed a real implementation of this seam, not a rewrite.
/// </para>
/// <para>
/// <b>The admin cookie is deliberately not consulted.</b> An administrator browsing the
/// storefront is not a buyer, and resolving them here would file their checkout against
/// a customer row that does not exist.
/// </para>
/// </summary>
public sealed class HttpCurrentCustomer(IHttpContextAccessor accessor) : ICurrentCustomer
{
    public string? IdentityUserId => Principal()?.FindFirstValue(ClaimTypes.NameIdentifier);

    public bool IsAuthenticated => IdentityUserId is { Length: > 0 };

    private ClaimsPrincipal? Principal()
    {
        var context = accessor.HttpContext;
        if (context is null) return null;

        /* Not context.User: that is whatever the default scheme produced, and a request
         * carrying only the admin cookie would otherwise read as a signed-in buyer. The
         * customer identity is the one with the customer scheme's authentication type. */
        var identity = context.User.Identities.FirstOrDefault(
            i => i.IsAuthenticated && i.AuthenticationType == NexusAuth.CustomerScheme);

        return identity is null ? null : new ClaimsPrincipal(identity);
    }
}
