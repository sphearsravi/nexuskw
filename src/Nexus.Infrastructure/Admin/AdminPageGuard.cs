using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Nexus.Domain.Entities;

namespace Nexus.Infrastructure.Admin;

/// <summary>
/// Puts the control panel's <b>pages</b> behind the admin cookie, not just its API.
/// <para>
/// The panel is static HTML in <c>wwwroot/admin</c>, and static files are served by
/// middleware that asks nobody for authorisation. Without this, a stranger could read
/// every admin page — empty of data, but a complete map of the API, the field names and
/// the actions. The data is already protected; this stops the reconnaissance, and stops
/// a signed-out operator staring at a panel that silently fails every request.
/// </para>
/// <para>
/// It is installed through an <see cref="IStartupFilter"/> so that
/// <c>AddNexusAdmin</c> is genuinely all the wiring there is: the filter runs ahead of
/// everything <c>Program.cs</c> configures, which is the only position from which it can
/// get in front of <c>UseStaticFiles</c>. Because it runs before
/// <c>UseAuthentication</c> it authenticates the admin scheme explicitly rather than
/// reading <c>HttpContext.User</c>.
/// </para>
/// </summary>
public static class AdminPageGuard
{
    public const string LoginPage = "/admin/login.html";

    /// <summary>
    /// True when this request is for an admin page that an anonymous visitor may not see.
    /// <para>
    /// The API is excluded because the controllers carry the real policies — Admin versus
    /// Staff, which this cannot know — and a blanket redirect in front of them would turn
    /// a 403 into a login page an XHR cannot read.
    /// </para>
    /// </summary>
    public static bool IsProtectedPage(PathString path)
    {
        if (!path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase, out var rest))
            return false;

        if (rest.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)) return false;

        var value = rest.Value ?? "";
        return !value.Equals("/login.html", StringComparison.OrdinalIgnoreCase);
    }

    public static IApplicationBuilder UseNexusAdminPages(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            if (!IsProtectedPage(ctx.Request.Path))
            {
                await next();
                return;
            }

            var result = await ctx.AuthenticateAsync(NexusAuth.AdminScheme);
            var staffOrBetter = result.Succeeded && result.Principal is { } who &&
                                (who.IsInRole(NexusRoles.Admin) || who.IsInRole(NexusRoles.Staff));

            if (staffOrBetter)
            {
                await next();
                return;
            }

            // An XHR wants a status code; a person typing the address wants the login page.
            if (ctx.Request.Headers.Accept.ToString()
                .Contains("application/json", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            ctx.Response.Redirect(LoginPage);
        });
}

/// <summary>Installs <see cref="AdminPageGuard"/> ahead of the static-file middleware.</summary>
public sealed class AdminPageGuardStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.UseNexusAdminPages();
            next(app);
        };
}
