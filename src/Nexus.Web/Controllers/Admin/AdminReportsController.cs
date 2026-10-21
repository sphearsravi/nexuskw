using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexus.Infrastructure.Admin;

namespace Nexus.Web.Controllers.Admin;

/// <summary>
/// Reporting. Read-only, so Staff may see it: knowing what sold is part of running the
/// shop, and nothing here changes anything.
/// </summary>
[ApiController]
[Route("admin/api/reports")]
[Authorize(Policy = AdminPolicies.AdminOrStaff)]
public sealed class AdminReportsController(AdminReportsService reports) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int days = 30, CancellationToken ct = default) =>
        Ok(await reports.BuildAsync(days, ct));

    /// <summary>
    /// The same figures as a spreadsheet. Every store eventually needs the numbers
    /// somewhere else, and without this the answer is retyping them.
    /// </summary>
    [HttpGet("export.csv")]
    public async Task<IActionResult> Export([FromQuery] int days = 30, CancellationToken ct = default)
    {
        var report = await reports.BuildAsync(days, ct);
        var name = $"nexus-report-{report.Range.From}-to-{report.Range.To}.csv";

        // A BOM so Excel opens UTF-8 correctly: without it the Arabic titles arrive as
        // mojibake on a Windows machine, which is where this file is going.
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(AdminReportsService.ToCsv(report)))
            .ToArray();

        return File(bytes, "text/csv; charset=utf-8", name);
    }
}
