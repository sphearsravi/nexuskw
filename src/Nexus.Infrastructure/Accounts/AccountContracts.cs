namespace Nexus.Infrastructure.Accounts;

/// <summary>
/// What a request asked for. Separate from the HTTP layer so the rules can be tested
/// without a controller, and so the controller has nothing to decide.
/// </summary>
public sealed record RegisterRequest(string? Name, string? Email, string? Password, string? Language);

public sealed record SignInRequest(string? Email, string? Password);

public sealed record ForgotPasswordRequest(string? Email, string? Language);

public sealed record ResetPasswordRequest(string? UserId, string? Token, string? Password);

public sealed record ConfirmEmailRequest(string? UserId, string? Token);

public sealed record ProfileUpdate(string? DisplayName, string? PreferredLanguage);

/// <summary>
/// Whether the message left the building. <see cref="Suppressed"/> is the honest answer
/// while <c>Email:Transport</c> resolves to None: the row exists, nothing was sent, and
/// the storefront says so rather than telling the customer to check an inbox that will
/// stay empty.
/// </summary>
public enum MailDelivery
{
    Queued = 0,
    Suppressed = 1
}

/// <summary>
/// The answer to register and to forgot-password.
/// <para>
/// <b>It carries no fact about whether the address exists.</b> Both endpoints return this
/// same shape with the same values whether the address was known, unknown, confirmed or
/// not — the only field that varies is <see cref="Delivery"/>, which depends on the
/// server's transport configuration and not on the request. That is the whole of the
/// anti-enumeration design, and it is why neither endpoint returns an error for a
/// duplicate address.
/// </para>
/// </summary>
public sealed record AcknowledgedResult(MailDelivery Delivery);

/// <summary>Why a request was refused. A code, never prose: the storefront owns every string a human reads.</summary>
public sealed record AccountProblem(string Code, string? Detail = null);

public sealed record SignedInCustomer(
    string UserId,
    string Email,
    string DisplayName,
    string PreferredLanguage,
    int? CustomerId);

/// <summary>What confirming an address did, including how much of a guest past it claimed (AC-7).</summary>
public sealed record ConfirmationResult(int CustomerId, int ClaimedOrders);
