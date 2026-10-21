using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure;

public static class IdentityRegistration
{
    /// <summary>
    /// One Identity store, three roles, and <b>two separate cookies</b>.
    /// <para>
    /// Customers and administrators never share a session. A stolen customer cookie
    /// loses one reader's books; a stolen admin cookie loses the catalogue, every
    /// order and every customer's email address. One cookie for both would make the
    /// cheap credential a route to the expensive one.
    /// </para>
    /// </summary>
    public static IServiceCollection AddNexusIdentity(this IServiceCollection services)
    {
        services
            .AddIdentityCore<NexusUser>(o =>
            {
                o.User.RequireUniqueEmail = true;

                // Confirmation is the gate on claiming a guest's past orders, so it
                // cannot be optional: without it, registering someone else's address
                // would hand over their purchases.
                o.SignIn.RequireConfirmedEmail = true;

                o.Password.RequiredLength = 10;
                o.Password.RequireNonAlphanumeric = false;   // length beats punctuation
                o.Password.RequireUppercase = false;
                o.Password.RequireDigit = false;

                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.Lockout.AllowedForNewUsers = true;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<NexusDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        services.AddAuthentication(NexusAuth.CustomerScheme)
            .AddCookie(NexusAuth.CustomerScheme, o =>
            {
                o.Cookie.Name = NexusAuth.CustomerScheme;
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.LoginPath = "/account.html";
                o.AccessDeniedPath = "/account.html";
                o.ExpireTimeSpan = TimeSpan.FromDays(30);
                o.SlidingExpiration = true;
                // A storefront fetch wants a status code, not a login page as a 200.
                o.Events.OnRedirectToLogin = ApiFriendly(StatusCodes.Status401Unauthorized);
                o.Events.OnRedirectToAccessDenied = ApiFriendly(StatusCodes.Status403Forbidden);
            })
            .AddCookie(NexusAuth.AdminScheme, o =>
            {
                o.Cookie.Name = NexusAuth.AdminScheme;
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;   // nothing links into the admin
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.Cookie.Path = "/admin";                  // never sent to storefront routes
                o.LoginPath = "/admin/login.html";
                o.AccessDeniedPath = "/admin/login.html";
                // Short, and not sliding: an unattended admin session should die.
                o.ExpireTimeSpan = TimeSpan.FromHours(8);
                o.SlidingExpiration = false;
                o.Events.OnRedirectToLogin = ApiFriendly(StatusCodes.Status401Unauthorized);
                o.Events.OnRedirectToAccessDenied = ApiFriendly(StatusCodes.Status403Forbidden);
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(NexusAuth.AdminOnly, p => p
                .AddAuthenticationSchemes(NexusAuth.AdminScheme)
                .RequireAuthenticatedUser()
                .RequireRole(NexusRoles.Admin))
            .AddPolicy(NexusAuth.AdminOrStaff, p => p
                .AddAuthenticationSchemes(NexusAuth.AdminScheme)
                .RequireAuthenticatedUser()
                .RequireRole(NexusRoles.Admin, NexusRoles.Staff))
            .AddPolicy(NexusAuth.CustomerOnly, p => p
                .AddAuthenticationSchemes(NexusAuth.CustomerScheme)
                .RequireAuthenticatedUser()
                .RequireRole(NexusRoles.Customer));

        services.AddScoped<IdentitySeeder>();

        return services;
    }

    /// <summary>
    /// An unauthenticated call to an API should get 401 or 403, not a 302 to a login page
    /// that the caller will try to parse as JSON.
    /// <para>
    /// Both prefixes matter. The storefront's API is under <c>/api</c>; the control
    /// panel's is under <c>/admin/api</c>, which the first check alone does not match —
    /// so an admin API call that did not happen to send an <c>Accept</c> header was
    /// being answered with a redirect to HTML. Access was refused either way; the shape
    /// of the refusal was wrong.
    /// </para>
    /// </summary>
    private static Func<RedirectContext<CookieAuthenticationOptions>, Task> ApiFriendly(int status) =>
        ctx =>
        {
            var path = ctx.Request.Path;
            var isApi = path.StartsWithSegments("/api")
                        || path.StartsWithSegments("/admin/api")
                        || ctx.Request.Headers.Accept.ToString()
                               .Contains("application/json", StringComparison.OrdinalIgnoreCase);

            if (isApi)
            {
                ctx.Response.StatusCode = status;
                return Task.CompletedTask;
            }

            ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
}

/// <summary>
/// Links a login to the customer row its orders hang on. Restrict, not cascade: deleting
/// a login must never silently take the order history with it.
/// </summary>
public sealed class NexusUserConfiguration : IEntityTypeConfiguration<NexusUser>
{
    public void Configure(EntityTypeBuilder<NexusUser> e)
    {
        e.Property(x => x.DisplayName).HasMaxLength(128);
        e.Property(x => x.PreferredLanguage).HasMaxLength(2);

        e.HasOne(x => x.Customer)
         .WithMany()
         .HasForeignKey(x => x.CustomerId)
         .OnDelete(DeleteBehavior.Restrict);

        e.HasIndex(x => x.CustomerId);
    }
}
