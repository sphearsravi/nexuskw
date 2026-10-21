using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Accounts;

/// <summary>
/// AC-7: a guest's past purchases join their account.
/// <para>
/// <b>This runs on confirmation and on nothing else.</b> Registering with an address does
/// not claim it; proving you can read mail sent to it does. Without that gate, typing a
/// stranger's address into the registration form would hand over their books — and the
/// attacker would not even need to receive the message, because the claim would already
/// have happened. The check is repeated here rather than left to the caller: a guard
/// stated once in a comment is a guard that the second caller does not have.
/// </para>
/// <para>
/// The merge itself is one assignment. Every guest checkout for an address resolves to
/// the <i>same</i> <c>Customer</c> row — <c>OrderService</c> looks it up by normalised
/// email — and orders hang on that row, so pointing it at the login claims the entire
/// history at once. There is no per-order loop to get wrong.
/// </para>
/// </summary>
public sealed partial class GuestOrderClaim(
    NexusDbContext db,
    UserManager<NexusUser> users,
    TimeProvider clock,
    ILogger<GuestOrderClaim> log)
{
    public async Task<(ConfirmationResult? Result, AccountProblem? Problem)> ClaimAsync(
        NexusUser user, CancellationToken ct = default)
    {
        if (!user.EmailConfirmed)
        {
            LogUnconfirmedClaim(log, user.Id);
            return (null, new AccountProblem("not_confirmed"));
        }

        if (string.IsNullOrWhiteSpace(user.Email)) return (null, new AccountProblem("no_email"));

        var normalised = Customer.Normalise(user.Email);
        var now = clock.GetUtcNow();

        var customer = await db.Customers.FirstOrDefaultAsync(c => c.NormalisedEmail == normalised, ct);

        if (customer is null)
        {
            customer = new Customer
            {
                Email = user.Email,
                NormalisedEmail = normalised,
                Name = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email : user.DisplayName,
                Country = "KW",
                PreferredLanguage = user.PreferredLanguage,
                IdentityUserId = user.Id,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Customers.Add(customer);
            await db.SaveChangesAsync(ct);

            await LinkLoginAsync(user, customer.Id);
            return (new ConfirmationResult(customer.Id, ClaimedOrders: 0), null);
        }

        if (customer.IdentityUserId is { Length: > 0 } owner && owner != user.Id)
        {
            /* Two logins for one address should be impossible — RequireUniqueEmail is on —
             * so this means a login was deleted and the row still points at it. Taking the
             * row anyway would move somebody's purchases; refusing leaves a confirmed
             * account with no library, which is visible and fixable. */
            LogAlreadyOwned(log, normalised, owner, user.Id);
            return (null, new AccountProblem("email_taken"));
        }

        // Counted before the link, so the number means "orders this account inherited"
        // rather than "orders it has", and a re-confirmation reports zero.
        var claimed = customer.IdentityUserId is null
            ? await db.Orders.CountAsync(o => o.CustomerId == customer.Id, ct)
            : 0;

        customer.IdentityUserId = user.Id;
        customer.UpdatedAt = now;

        // The display name is the account holder's to set; the orders keep their own
        // snapshots, so nothing already sold is rewritten by this.
        if (!string.IsNullOrWhiteSpace(user.DisplayName)) customer.Name = user.DisplayName;

        await db.SaveChangesAsync(ct);
        await LinkLoginAsync(user, customer.Id);

        return (new ConfirmationResult(customer.Id, claimed), null);
    }

    private async Task LinkLoginAsync(NexusUser user, int customerId)
    {
        if (user.CustomerId == customerId) return;

        user.CustomerId = customerId;
        var updated = await users.UpdateAsync(user);

        if (!updated.Succeeded)
            LogLinkFailed(log, user.Id, string.Join("; ", updated.Errors.Select(e => e.Description)));
    }

    [LoggerMessage(EventId = 6101, Level = LogLevel.Error,
        Message = "Refused to claim guest orders for user {UserId}: the address is not confirmed.")]
    private static partial void LogUnconfirmedClaim(ILogger logger, string userId);

    [LoggerMessage(EventId = 6102, Level = LogLevel.Error,
        Message = "Customer {NormalisedEmail} is already owned by login {Owner}; {Claimant} did not take it.")]
    private static partial void LogAlreadyOwned(ILogger logger, string normalisedEmail, string owner, string claimant);

    [LoggerMessage(EventId = 6103, Level = LogLevel.Error,
        Message = "Could not link login {UserId} to its customer row: {Errors}")]
    private static partial void LogLinkFailed(ILogger logger, string userId, string errors);
}
