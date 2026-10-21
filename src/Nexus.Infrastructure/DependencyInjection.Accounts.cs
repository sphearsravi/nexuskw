using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Application.Email;
using Nexus.Application.Orders;
using Nexus.Infrastructure.Accounts;
using Nexus.Infrastructure.Email;

namespace Nexus.Infrastructure;

/// <summary>
/// Phase 6's wiring, in its own file so parallel phases do not fight over one method.
/// <para>
/// <b>Call this after <c>AddNexusOrders</c> and <c>AddNexusEmail</c>.</b> It replaces two
/// placeholders those two installed — the guest stand-in for "who is signed in", and the
/// order renderer that answers <c>unsupported_kind</c> for the two account emails — and a
/// replacement only replaces what is already there.
/// </para>
/// <para>
/// It adds no Identity configuration. <c>AddNexusIdentity</c> owns the store, the three
/// roles, the two cookies and the three policies; a second configuration here would be a
/// second set of password rules and a second answer to what confirmation means.
/// </para>
/// </summary>
public static class AccountsServiceCollectionExtensions
{
    public static IServiceCollection AddNexusAccounts(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();

        /* Removed rather than appended. Appending wins only because this call comes
         * later, which makes the correctness of sign-in a property of the order of lines
         * in Program.cs — and that is not a thing anyone checks when they move one. */
        services.RemoveAll<ICurrentCustomer>();
        services.AddScoped<ICurrentCustomer, HttpCurrentCustomer>();

        services.AddScoped<GuestOrderClaim>();
        services.AddScoped<AccountMailer>();
        services.AddScoped<AccountService>();
        services.AddScoped<LibraryService>();

        /* The account emails are rendered by a decorator over the order renderer, so the
         * email stream's file is not edited by this phase and the dispatcher keeps one
         * IEmailTemplateRenderer to resolve. The inner instance is constructed, not
         * resolved, because resolving the interface here is how a decorator wraps
         * itself. */
        services.RemoveAll<IEmailTemplateRenderer>();
        services.AddScoped<IEmailTemplateRenderer>(sp => new AccountEmailRenderer(
            ActivatorUtilities.CreateInstance<EmailTemplateRenderer>(sp),
            sp.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Domain.Entities.NexusUser>>(),
            sp.GetRequiredService<Application.Common.ISettingsReader>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SiteOptions>>()));

        return services;
    }
}
