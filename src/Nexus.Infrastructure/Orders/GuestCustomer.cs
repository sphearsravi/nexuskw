using Nexus.Application.Orders;

namespace Nexus.Infrastructure.Orders;

/// <summary>
/// Nobody is signed in. There is no Identity store until Phase 6, so every checkout in
/// these phases is a guest checkout.
/// <para>
/// The seam exists now so Phase 6 adds an <c>HttpContext</c>-backed implementation and
/// registers it over this one, rather than rewriting <c>OrderService</c>. The signed-in
/// branch in <see cref="OrderService"/> is already written and already tested against a
/// fake, because the rule it enforces — an authenticated request's body may not name
/// the buyer — is the same class of bug as a client-supplied total.
/// </para>
/// </summary>
public sealed class GuestCustomer : ICurrentCustomer
{
    public string? IdentityUserId => null;

    public bool IsAuthenticated => false;
}
