using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexus.Domain;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Email;
using Nexus.Infrastructure.Orders;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Accounts;

/// <summary>A successful sign-in: the cookie's principal, and what the page shows.</summary>
public sealed record SignInSuccess(ClaimsPrincipal Principal, SignedInCustomer Customer);

/// <summary>
/// Registration, confirmation, sign-in and password reset.
/// <para>
/// <b>Register and forgot-password are deliberately incapable of refusing.</b> Both
/// return the same acknowledgement whatever they find, because an endpoint that says
/// "that address is taken" is a list of this store's customers, readable by anyone with
/// a word list. The customer who already has an account is told, by email, that someone
/// tried — which reaches the right person instead of the wrong one.
/// </para>
/// <para>
/// Sign-in is allowed to be specific, but only once the password is proved. "Confirm your
/// email first" and "this account is locked" are both disclosures, so neither is said to
/// a caller who could not produce the password.
/// </para>
/// </summary>
public sealed partial class AccountService(
    UserManager<NexusUser> users,
    SignInManager<NexusUser> signIn,
    IUserClaimsPrincipalFactory<NexusUser> claims,
    IOptions<IdentityOptions> identity,
    GuestOrderClaim claim,
    AccountMailer mailer,
    NexusDbContext db,
    TimeProvider clock,
    ILogger<AccountService> log)
{
    public const int MinNameLength = 2;
    public const int MaxNameLength = 128;
    public const int MaxEmailLength = 256;

    // ---------------------------------------------------------------- register

    public async Task<(AcknowledgedResult? Result, AccountProblem? Problem)> RegisterAsync(
        RegisterRequest request, CancellationToken ct = default)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length is < MinNameLength or > MaxNameLength)
            return (null, new AccountProblem("invalid_name"));

        if (!CheckoutNormalisation.IsPlausibleEmail(request.Email, MaxEmailLength))
            return (null, new AccountProblem("invalid_email"));

        var email = request.Email!.Trim();
        var language = Lang.Normalise(request.Language);

        /* Password rules are checked BEFORE the address is looked up, against a user that
         * is never saved. Checking them afterwards would split the two branches: a weak
         * password on a free address would fail while the same password on a taken one
         * succeeded, and that difference is the enumeration oracle this endpoint exists
         * to avoid. */
        var candidate = new NexusUser { UserName = email, Email = email };
        if (await PasswordProblemAsync(candidate, request.Password) is { } weak) return (null, weak);

        var existing = await users.FindByEmailAsync(email);
        if (existing is not null)
        {
            /* Hash anyway. The create path below spends tens of milliseconds in PBKDF2,
             * and skipping it here would make "this address is taken" measurable with a
             * stopwatch. */
            _ = users.PasswordHasher.HashPassword(existing, request.Password!);

            if (!existing.EmailConfirmed)
            {
                // A genuine customer who lost the first message. Still no account facts
                // in the response; the mail goes to the address, which is the only place
                // it is safe to say anything.
                await mailer.QueueAsync(EmailKind.AccountConfirm, existing, language, ct);
            }
            else
            {
                LogRegisterOnExisting(log, existing.Id);
            }

            return (new AcknowledgedResult(mailer.Delivery), null);
        }

        var user = new NexusUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = false,
            DisplayName = name,
            PreferredLanguage = language,
            CreatedAt = clock.GetUtcNow()
        };

        var created = await users.CreateAsync(user, request.Password!);
        if (!created.Succeeded)
        {
            /* A race with another registration for the same address lands here. The
             * answer is still the acknowledgement: the caller must not learn the
             * difference between "you created it" and "it already existed". */
            LogCreateFailed(log, string.Join("; ", created.Errors.Select(e => e.Description)));
            return (new AcknowledgedResult(mailer.Delivery), null);
        }

        await users.AddToRoleAsync(user, NexusRoles.Customer);

        // No Customer row and no claim yet. That is AC-7's gate expressed as structure
        // rather than as a condition: there is nothing here for an unconfirmed
        // registration to inherit.
        await mailer.QueueAsync(EmailKind.AccountConfirm, user, language, ct);

        return (new AcknowledgedResult(mailer.Delivery), null);
    }

    // ----------------------------------------------------------------- confirm

    public async Task<(ConfirmationResult? Result, AccountProblem? Problem)> ConfirmAsync(
        ConfirmEmailRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.UserId)) return (null, new AccountProblem("invalid_token"));
        if (AccountTokens.Decode(request.Token) is not { } token) return (null, new AccountProblem("invalid_token"));

        var user = await users.FindByIdAsync(request.UserId);
        if (user is null) return (null, new AccountProblem("invalid_token"));

        // Clicking the link twice is the commonest way to reach this page. The second
        // click carries a spent token and should land on a confirmed account, not an error.
        if (!user.EmailConfirmed)
        {
            var confirmed = await users.ConfirmEmailAsync(user, token);
            if (!confirmed.Succeeded) return (null, new AccountProblem("invalid_token"));
        }

        return await claim.ClaimAsync(user, ct);
    }

    // ----------------------------------------------------------------- sign in

    public async Task<(SignInSuccess? Success, AccountProblem? Problem)> SignInAsync(
        SignInRequest request, CancellationToken ct = default)
    {
        var generic = new AccountProblem("invalid_credentials");

        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
            return (null, generic);

        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null)
        {
            // Spend the time the real path spends, so a wrong address is not faster
            // than a wrong password.
            _ = users.PasswordHasher.VerifyHashedPassword(
                new NexusUser(), EmptyPasswordHash.Value, request.Password);
            return (null, generic);
        }

        var attempt = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);

        if (attempt.Succeeded)
        {
            // AC-8. An administrator's login is not a storefront login; letting one open
            // a customer session would put both audiences back on one credential.
            if (!await users.IsInRoleAsync(user, NexusRoles.Customer))
                return (null, new AccountProblem("not_a_customer"));

            await users.ResetAccessFailedCountAsync(user);
            return (new SignInSuccess(await PrincipalAsync(user), Describe(user)), null);
        }

        /* Lockout and not-confirmed are both facts about an account that exists, so
         * neither is reported to someone who could not produce the password. The password
         * is re-checked rather than inferred: CheckPasswordSignInAsync runs its
         * pre-sign-in checks first and returns NotAllowed without ever looking at it. */
        var passwordIsRight = await users.CheckPasswordAsync(user, request.Password);

        if (!passwordIsRight)
        {
            // CheckPasswordSignInAsync counted nothing on the NotAllowed path, so an
            // unconfirmed account would otherwise be an unthrottled guessing target.
            if (attempt.IsNotAllowed) await users.AccessFailedAsync(user);
            return (null, generic);
        }

        if (attempt.IsLockedOut) return (null, new AccountProblem("locked_out"));
        if (attempt.IsNotAllowed) return (null, new AccountProblem("email_not_confirmed"));

        return (null, generic);
    }

    public async Task<ClaimsPrincipal> PrincipalAsync(NexusUser user)
    {
        var principal = await claims.CreateAsync(user);

        /* Rebuilt with the customer scheme as its authentication type. The factory stamps
         * Identity.Application on it, which is a scheme this application does not
         * register, and HttpCurrentCustomer tells a buyer from an administrator by
         * exactly that value. */
        var options = identity.Value.ClaimsIdentity;
        var rebuilt = new ClaimsIdentity(
            principal.Claims, NexusAuth.CustomerScheme, options.UserNameClaimType, options.RoleClaimType);

        return new ClaimsPrincipal(rebuilt);
    }

    // ------------------------------------------------------- forgot and reset

    /// <summary>
    /// Always succeeds, and always says the same thing. An unknown address, a known one
    /// and an unconfirmed one are indistinguishable from outside.
    /// </summary>
    public async Task<AcknowledgedResult> ForgotPasswordAsync(
        ForgotPasswordRequest request, CancellationToken ct = default)
    {
        var acknowledged = new AcknowledgedResult(mailer.Delivery);

        if (!CheckoutNormalisation.IsPlausibleEmail(request.Email, MaxEmailLength)) return acknowledged;

        var user = await users.FindByEmailAsync(request.Email!.Trim());

        /* An unconfirmed account gets a confirmation link instead. Resetting the password
         * on an address nobody has proved they can read would be a way to finish taking
         * over an account registered with someone else's address. */
        if (user is not null)
        {
            var language = Lang.Normalise(request.Language ?? user.PreferredLanguage);
            await mailer.QueueAsync(
                user.EmailConfirmed ? EmailKind.PasswordReset : EmailKind.AccountConfirm, user, language, ct);
        }

        return acknowledged;
    }

    public async Task<(bool Reset, AccountProblem? Problem)> ResetPasswordAsync(
        ResetPasswordRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.UserId)) return (false, new AccountProblem("invalid_token"));
        if (AccountTokens.Decode(request.Token) is not { } token) return (false, new AccountProblem("invalid_token"));

        var user = await users.FindByIdAsync(request.UserId);
        if (user is null) return (false, new AccountProblem("invalid_token"));

        if (await PasswordProblemAsync(user, request.Password) is { } weak) return (false, weak);

        var reset = await users.ResetPasswordAsync(user, token, request.Password!);
        if (!reset.Succeeded) return (false, new AccountProblem("invalid_token"));

        /* Reading the reset mail proves as much as reading the confirmation mail, and a
         * reset that left the account unconfirmed would strand the customer at a sign-in
         * they can never pass. The guest-order claim therefore runs here too — and only
         * ever behind a proof that the mailbox was read. */
        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            await users.UpdateAsync(user);
            await claim.ClaimAsync(user, ct);
        }

        return (true, null);
    }

    // ------------------------------------------------------------------ profile

    public async Task<SignedInCustomer?> ProfileAsync(string userId)
    {
        var user = await users.FindByIdAsync(userId);
        return user is null ? null : Describe(user);
    }

    public async Task<(SignedInCustomer? Profile, AccountProblem? Problem)> UpdateProfileAsync(
        string userId, ProfileUpdate update, CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is null) return (null, new AccountProblem("not_found"));

        var name = (update.DisplayName ?? string.Empty).Trim();
        if (name.Length is < MinNameLength or > MaxNameLength) return (null, new AccountProblem("invalid_name"));

        user.DisplayName = name;
        user.PreferredLanguage = Lang.Normalise(update.PreferredLanguage);

        var saved = await users.UpdateAsync(user);
        if (!saved.Succeeded) return (null, new AccountProblem("not_saved"));

        /* AC-6 says the preference follows the customer to any device, so it is written
         * where a background job can read it: the dispatcher has no session, and an email
         * sent at three in the morning takes its language from here. */
        if (user.CustomerId is { } customerId)
        {
            var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == customerId, ct);
            if (customer is not null)
            {
                customer.Name = name;
                customer.PreferredLanguage = user.PreferredLanguage;
                customer.UpdatedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct);
            }
        }

        return (Describe(user), null);
    }

    // ------------------------------------------------------------------ shared

    private static SignedInCustomer Describe(NexusUser user) =>
        new(user.Id,
            user.Email ?? "",
            string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email ?? "" : user.DisplayName,
            Lang.Normalise(user.PreferredLanguage),
            user.CustomerId);

    private async Task<AccountProblem?> PasswordProblemAsync(NexusUser user, string? password)
    {
        if (string.IsNullOrEmpty(password)) return new AccountProblem("weak_password");

        foreach (var validator in users.PasswordValidators)
        {
            var result = await validator.ValidateAsync(users, user, password);
            if (!result.Succeeded)
            {
                // Identity's own codes, passed through for the log and for a developer.
                // The storefront shows its own sentence; it never prints this.
                return new AccountProblem("weak_password", string.Join(" ", result.Errors.Select(e => e.Code)));
            }
        }

        return null;
    }

    [LoggerMessage(EventId = 6110, Level = LogLevel.Information,
        Message = "Registration attempted against existing confirmed account {UserId}; acknowledged without changes.")]
    private static partial void LogRegisterOnExisting(ILogger logger, string userId);

    [LoggerMessage(EventId = 6111, Level = LogLevel.Warning,
        Message = "Could not create the account; the caller was acknowledged anyway: {Errors}")]
    private static partial void LogCreateFailed(ILogger logger, string errors);
}

/// <summary>
/// A hash of a password nobody holds, so a sign-in for an address that does not exist
/// spends the same time as one that does. Built at startup from fresh random bytes: a
/// committed hash in a public repository is a published credential even when it is a
/// decoy, and this way there is nothing to commit.
/// </summary>
internal static class EmptyPasswordHash
{
    public static string Value { get; } = new PasswordHasher<NexusUser>()
        .HashPassword(new NexusUser(), Convert.ToBase64String(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
}
