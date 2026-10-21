using Microsoft.AspNetCore.Mvc;
using Nexus.Infrastructure.Settings;

namespace Nexus.Web.Controllers;

/// <summary>
/// The handful of operator-editable numbers the storefront is allowed to know, so
/// "seven days, three downloads" stops being hardcoded in three pages.
/// </summary>
[ApiController]
[Route("api/settings")]
public sealed class PublicSettingsController(IPublicSettingsReader settings) : ControllerBase
{
    /// <remarks>
    /// The reader walks a hardcoded allow-list, never a projection over the table. The
    /// same table holds the Tap secret key in Phase 7, and a projection would publish it
    /// the day somebody added the row.
    /// </remarks>
    [HttpGet("public")]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Client)]
    public async Task<IReadOnlyDictionary<string, string>> Public(CancellationToken ct) =>
        await settings.ReadPublicAsync(ct);
}
