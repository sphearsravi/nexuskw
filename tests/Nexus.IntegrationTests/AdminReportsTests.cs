using System.Net;
using System.Text.Json;

namespace Nexus.IntegrationTests;

/// <summary>
/// Reporting. The figures must be counted rather than estimated, and the endpoint must
/// sit behind the admin cookie like every other panel surface.
/// <para>
/// Uses Phase 7's own host and cookie helpers rather than a second set: one way of
/// authenticating a panel test is easier to keep correct than two.
/// </para>
/// </summary>
[Collection(NexusCollection.Name)]
public sealed class AdminReportsTests(NexusApp app) : IDisposable
{
    private readonly AdminHostP7 _host = new(app);

    public void Dispose() => _host.Dispose();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task Reports_refuse_an_anonymous_caller()
    {
        var response = await _host.Anonymous().GetAsync("/admin/api/reports");
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"answered {(int)response.StatusCode}");
    }

    /// <summary>A real customer session must not open the panel's reports either.</summary>
    [Fact]
    public async Task A_customer_session_cannot_read_the_reports()
    {
        var response = await _host.AsCustomer().GetAsync("/admin/api/reports");
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Staff may read them. Knowing what sold is part of running the shop, and nothing
    /// on this screen changes anything.
    /// </summary>
    [Fact]
    public async Task Staff_may_read_the_reports()
    {
        var response = await _host.AsStaff().GetAsync("/admin/api/reports?days=7");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The figures hold together whatever the data happens to be.
    /// <para>
    /// This deliberately asserts invariants rather than an empty database. The suite
    /// shares one database and other tests create paid orders, so an assertion that
    /// revenue is exactly zero passes alone and fails in the full run — which says
    /// something about the test, not about the report.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_figures_are_internally_consistent()
    {
        var report = await JsonOf(await _host.AsAdmin().GetAsync("/admin/api/reports?days=7"));

        var revenue = report.GetProperty("revenueKwd").GetDecimal();
        var paid = report.GetProperty("paidOrders").GetInt32();
        var all = report.GetProperty("allOrders").GetInt32();
        var average = report.GetProperty("averageOrderKwd").GetDecimal();

        Assert.True(revenue >= 0, "revenue went negative");
        Assert.True(paid <= all, "more orders were paid than were placed");

        if (paid == 0)
            Assert.Equal(0m, average);
        else
            // Rounded at the currency's own scale, so allow one fils of rounding.
            Assert.True(Math.Abs(average - revenue / paid) <= 0.001m,
                $"average {average} does not follow from {revenue} over {paid}");

        // Seven days including the quiet ones: a series that skips empty days draws a
        // chart that flatters the shop.
        Assert.Equal(7, report.GetProperty("daily").GetArrayLength());

        // And every amount is KWD at three decimals, like everywhere else.
        foreach (var point in report.GetProperty("daily").EnumerateArray())
            Assert.Equal(point.GetProperty("revenueKwd").GetDecimal(),
                         decimal.Round(point.GetProperty("revenueKwd").GetDecimal(), 3));
    }

    /// <summary>
    /// Every title is unsold while nothing has been paid for. That is the honest answer
    /// and a useful one — it is the list of what to reprice, rewrite or withdraw.
    /// </summary>
    [Fact]
    public async Task Never_sold_lists_titles_when_nothing_has_sold()
    {
        var report = await JsonOf(await _host.AsAdmin().GetAsync("/admin/api/reports?days=30"));
        Assert.True(report.GetProperty("neverSold").GetArrayLength() > 0);
    }

    [Theory]
    [InlineData(99999, 365)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public async Task The_range_is_clamped(int asked, int expected)
    {
        var report = await JsonOf(await _host.AsAdmin().GetAsync($"/admin/api/reports?days={asked}"));
        Assert.Equal(expected, report.GetProperty("range").GetProperty("days").GetInt32());
    }

    /// <summary>
    /// The export lands in a spreadsheet on a Windows machine, so it carries a UTF-8
    /// BOM: without one Excel renders the Arabic titles as mojibake.
    /// </summary>
    [Fact]
    public async Task The_csv_is_utf8_with_a_bom_and_an_invariant_decimal_point()
    {
        var response = await _host.AsAdmin().GetAsync("/admin/api/reports/export.csv?days=7");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "the export has no UTF-8 BOM, so Excel will mangle the Arabic titles");

        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("date,revenue_kwd,orders", text, StringComparison.Ordinal);

        // Three decimals with a dot, whatever culture the server happens to run under.
        Assert.Matches(@"\d{4}-\d{2}-\d{2},\d+\.\d{3},\d+", text);
    }

    [Fact]
    public async Task The_csv_export_refuses_an_anonymous_caller()
    {
        var response = await _host.Anonymous().GetAsync("/admin/api/reports/export.csv");
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }
}
