using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Application.Common;
using Nexus.Infrastructure.Admin;

namespace Nexus.Infrastructure;

/// <summary>
/// Phase 7's wiring, in its own file so parallel streams never edit one method.
/// <c>Program.cs</c> calls <see cref="AddNexusAdmin"/> once, after
/// <c>AddNexusIdentity</c>, <c>AddNexusDelivery</c> and <c>AddNexusEmail</c>: the control
/// panel drives those services rather than reimplementing them.
/// </summary>
public static class AdminRegistration
{
    /// <remarks>
    /// <para>
    /// <b>Nothing else is needed in <c>Program.cs</c>.</b> The one piece of middleware the
    /// panel requires — the guard that keeps anonymous visitors out of the static admin
    /// pages — installs itself through an <see cref="IStartupFilter"/>, because it has to
    /// run before <c>UseStaticFiles</c> and that position is not reachable from a service
    /// registration any other way. See <see cref="AdminPageGuard"/>.
    /// </para>
    /// <para>
    /// <see cref="IAuditLog"/> is registered here and nowhere else. It was declared in
    /// <c>Nexus.Application.Common</c> by the phases 2–5 plan and never implemented; every
    /// administrative write in this phase goes through it.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddNexusAdmin(this IServiceCollection services)
    {
        // The audit trail needs to know who is acting, and the only honest source is the
        // current request.
        services.AddHttpContextAccessor();
        services.TryAddScoped<IAdminActorAccessor, HttpAdminActorAccessor>();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IAuditLog, AuditLogWriter>();

        services.AddScoped<AdminDashboardService>();
        services.AddScoped<BookAdminService>();
        services.AddScoped<CatalogueImportService>();
        services.AddScoped<CategoryAdminService>();
        services.AddScoped<OrderAdminService>();
        services.AddScoped<CustomerAdminService>();
        services.AddScoped<DownloadAdminService>();
        services.AddScoped<AdminSettingsService>();

        services.AddTransient<IStartupFilter, AdminPageGuardStartupFilter>();

        return services;
    }
}
