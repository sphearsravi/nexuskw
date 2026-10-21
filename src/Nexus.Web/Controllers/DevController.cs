using Microsoft.AspNetCore.Mvc;

namespace Nexus.Web.Controllers;

public sealed record DemoAccount(string Label, string Email, string Password);

/// <summary>
/// Convenience for local work: the demo credentials the sign-in pages offer as a
/// one-click fill.
/// <para>
/// <b>It answers 404 outside Development, before it reads anything.</b> The credentials
/// are never compiled into a page, because a page ships; they are served only by an
/// environment that is not production, so there is nothing to leak and nothing to
/// remember to remove. The sign-in pages show the button only when this endpoint
/// answers, so production simply has no button.
/// </para>
/// <para>
/// Also: these are the same values a developer already has in
/// <c>appsettings.Development.json</c>. This endpoint discloses nothing that reading the
/// repository would not.
/// </para>
/// </summary>
[ApiController]
[Route("api/dev")]
public sealed class DevController(IWebHostEnvironment env, IConfiguration config) : ControllerBase
{
    [HttpGet("demo-accounts")]
    public IActionResult DemoAccounts()
    {
        if (!env.IsDevelopment()) return NotFound();

        var accounts = new List<DemoAccount>();

        var adminEmail = config["Admin:Email"];
        var adminPassword = config["Admin:Password"];
        if (!string.IsNullOrWhiteSpace(adminEmail) && !string.IsNullOrWhiteSpace(adminPassword))
            accounts.Add(new DemoAccount("admin", adminEmail, adminPassword));

        var customerEmail = config["Demo:CustomerEmail"];
        var customerPassword = config["Demo:CustomerPassword"];
        if (!string.IsNullOrWhiteSpace(customerEmail) && !string.IsNullOrWhiteSpace(customerPassword))
            accounts.Add(new DemoAccount("customer", customerEmail, customerPassword));

        return Ok(accounts);
    }
}
