using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nexus.Domain.Entities;

namespace Nexus.Infrastructure.Persistence;

/// <summary>
/// Creates the three roles, and the first administrator so the control panel is
/// reachable at all on a fresh database.
/// <para>
/// The first admin's password comes from configuration — an App Pool environment
/// variable on the server — and there is no default. A seeded default password is a
/// published credential the day the repository is public, which this one is.
/// </para>
/// </summary>
public sealed class IdentitySeeder(
    RoleManager<IdentityRole> roles,
    UserManager<NexusUser> users,
    IConfiguration config,
    ILogger<IdentitySeeder> log)
{
    public async Task SeedAsync(bool isProduction, CancellationToken ct = default)
    {
        foreach (var role in NexusRoles.All)
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new IdentityRole(role));

        var email = config["Admin:Email"];
        var password = config["Admin:Password"];

        if (string.IsNullOrWhiteSpace(email))
        {
            log.LogInformation("No Admin:Email configured; no administrator seeded.");
            return;
        }

        if (await users.FindByEmailAsync(email) is not null) return;

        if (string.IsNullOrWhiteSpace(password))
        {
            // Refusing beats inventing one. An unreachable admin panel is a visible
            // problem; a guessable administrator is an invisible one.
            log.LogError(
                "Admin:Email is set but Admin:Password is not. No administrator was created. " +
                "Set both, as App Pool environment variables in production.");
            return;
        }

        if (isProduction && password.Length < 16)
        {
            log.LogError(
                "Admin:Password is shorter than 16 characters. Refusing to seed an " +
                "administrator in production.");
            return;
        }

        var admin = new NexusUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = "Administrator",
            CreatedAt = DateTimeOffset.UtcNow
        };

        var created = await users.CreateAsync(admin, password);
        if (!created.Succeeded)
        {
            log.LogError("Could not seed the administrator: {Errors}",
                string.Join("; ", created.Errors.Select(e => e.Description)));
            return;
        }

        await users.AddToRoleAsync(admin, NexusRoles.Admin);
        log.LogInformation("Seeded the first administrator, {Email}.", email);

        await SeedDemoCustomerAsync(isProduction);
    }

    /// <summary>
    /// A ready, already-confirmed customer so the storefront can be signed into without
    /// going through an email round trip.
    /// <para>
    /// Refused outright in production. A known account with a known password on a live
    /// store is a back door, however it is labelled, and this one would be published in
    /// the repository's own configuration.
    /// </para>
    /// </summary>
    private async Task SeedDemoCustomerAsync(bool isProduction)
    {
        if (isProduction) return;

        var email = config["Demo:CustomerEmail"];
        var password = config["Demo:CustomerPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) return;
        if (await users.FindByEmailAsync(email) is not null) return;

        var demo = new NexusUser
        {
            UserName = email,
            Email = email,
            // Confirmed deliberately: the point is to skip the round trip, and the
            // confirmation gate still governs guest-order claiming for real accounts.
            EmailConfirmed = true,
            DisplayName = "Demo Buyer",
            CreatedAt = DateTimeOffset.UtcNow
        };

        var created = await users.CreateAsync(demo, password);
        if (!created.Succeeded)
        {
            log.LogWarning("Could not seed the demo customer: {Errors}",
                string.Join("; ", created.Errors.Select(e => e.Description)));
            return;
        }

        await users.AddToRoleAsync(demo, NexusRoles.Customer);
        log.LogInformation("Seeded the demo customer, {Email}. Development only.", email);
    }
}
