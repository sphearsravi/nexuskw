using System.Net;
using Microsoft.EntityFrameworkCore;
using Nexus.Application.Delivery;
using Nexus.Domain;
using Nexus.Infrastructure.Persistence;

namespace Nexus.IntegrationTests;

/// <summary>
/// The download endpoint, against real SQL Server.
/// <para>
/// The counter test in particular <b>must</b> run here: the guarantee that two
/// concurrent requests on a three-use grant cannot yield four downloads comes from the
/// engine's row locking, so an in-memory provider would pass it for the wrong reason and
/// the bug would ship.
/// </para>
/// </summary>
[Collection(NexusCollection.Name)]
public sealed class DeliveryDownloadTests(NexusApp app) : IDisposable
{
    private readonly DeliveryHost _host = new(app);

    public void Dispose() => _host.Dispose();

    private async Task<string> GrantAsync(string tag, int bookId, Action<Nexus.Domain.Entities.DownloadGrant>? adjust = null)
    {
        var (order, _) = await _host.PaidOrderAsync(tag, bookId);

        using var scope = _host.Scope();
        var delivery = _host.Service<IDeliveryService>(scope);
        var issued = await delivery.IssueGrantsForOrderAsync(order.Id);
        var token = issued.Single().Token;

        if (adjust is not null)
        {
            var db = _host.Service<NexusDbContext>(scope);
            var grant = await db.DownloadGrants.FirstAsync(g => g.Id == issued[0].Grant.Id);
            adjust(grant);
            await db.SaveChangesAsync();
        }

        return token;
    }

    [Fact]
    public async Task A_valid_link_serves_the_pdf()
    {
        var token = await GrantAsync("dla001", 1);

        var response = await _host.Client.GetAsync($"/download/{token}");
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(bytes[..5]), StringComparison.Ordinal);

        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.Equal("none", response.Headers.AcceptRanges.Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("attachment", response.Content.Headers.ContentDisposition!.ToString(), StringComparison.Ordinal);
        Assert.Contains(".pdf", response.Content.Headers.ContentDisposition!.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Eight genuinely parallel requests against a grant with one use left. Exactly one
    /// may win. This is the test the whole atomic-increment design exists for.
    /// </summary>
    [Fact]
    public async Task Concurrent_requests_cannot_overspend_a_grant()
    {
        var token = await GrantAsync("dlc001", 2, g => g.UsedCount = g.MaxDownloads - 1);

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => _host.Client.GetAsync($"/download/{token}")));

        var served = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var refused = responses.Count(r => r.StatusCode == HttpStatusCode.Gone);

        Assert.Equal(1, served);
        Assert.Equal(7, refused);

        using var scope = _host.Scope();
        var db = _host.Service<NexusDbContext>(scope);
        var grant = await db.DownloadGrants.AsNoTracking().FirstAsync(g => g.TokenHash == Hash(token));

        Assert.Equal(grant.MaxDownloads, grant.UsedCount);
        Assert.Equal(GrantState.Exhausted, grant.State);
    }

    /// <summary>
    /// The same burst against a fresh three-use grant: exactly three win, and the fourth
    /// through eighth are refused. One winner could be an accident of scheduling; three
    /// out of eight is the counter doing its job.
    /// </summary>
    [Fact]
    public async Task A_burst_hands_out_exactly_the_allowance_and_no_more()
    {
        var token = await GrantAsync("dlb001", 16);

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => _host.Client.GetAsync($"/download/{token}")));

        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.Gone));

        using var scope = _host.Scope();
        var db = _host.Service<NexusDbContext>(scope);
        Assert.Equal(3, (await db.DownloadGrants.AsNoTracking().FirstAsync(g => g.TokenHash == Hash(token))).UsedCount);

        foreach (var response in responses) response.Dispose();
    }

    [Fact]
    public async Task A_grant_walks_to_exhaustion_and_then_refuses()
    {
        var token = await GrantAsync("dle001", 3);

        for (var i = 0; i < 3; i++)
        {
            using var ok = await _host.Client.GetAsync($"/download/{token}");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var gone = await _host.Client.GetAsync($"/download/{token}");
        Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);
        Assert.Contains("exhausted", await gone.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var scope = _host.Scope();
        var db = _host.Service<NexusDbContext>(scope);
        Assert.Equal(
            GrantState.Exhausted,
            (await db.DownloadGrants.AsNoTracking().FirstAsync(g => g.TokenHash == Hash(token))).State);
    }

    /// <summary>
    /// Expiry is refused by the download path itself, not by the nightly sweep: a grant
    /// that lapsed four minutes ago has not been swept yet and must still be refused.
    /// </summary>
    [Fact]
    public async Task An_expired_grant_is_refused_without_waiting_for_the_sweep()
    {
        var token = await GrantAsync("dlx001", 4, g => g.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1));

        using var response = await _host.Client.GetAsync($"/download/{token}");

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Contains("expired", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_revoked_grant_is_refused()
    {
        var token = await GrantAsync("dlr001", 5, g =>
        {
            g.State = GrantState.Revoked;
            g.RevocationReason = RevocationReason.Refunded;
        });

        using var response = await _host.Client.GetAsync($"/download/{token}");

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Contains("revoked", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A token is the credential, so an unknown one must be cheap for us and silent to
    /// the holder — and above all must not let an enumerator grow our log table.
    /// </summary>
    [Theory]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]   // well-formed, unknown
    [InlineData("short")]
    [InlineData("..%2f..%2fApp_Data%2flibrary%2f1%2fx.pdf")]
    [InlineData("....//....//appsettings.json")]
    public async Task An_unknown_or_malformed_token_is_a_bare_404(string token)
    {
        int before;
        using (var scope = _host.Scope())
        {
            before = await _host.Service<NexusDbContext>(scope).DownloadLogs.CountAsync();
        }

        using var response = await _host.Client.GetAsync($"/download/{token}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var after = _host.Scope();
        Assert.Equal(before, await _host.Service<NexusDbContext>(after).DownloadLogs.CountAsync());
    }

    /// <summary>
    /// ASP.NET Core maps HEAD onto GET handlers. Left alone, a link checker following
    /// the emailed URL spends one of three downloads before the buyer reads the message.
    /// </summary>
    [Fact]
    public async Task A_head_request_is_refused_and_costs_nothing()
    {
        var token = await GrantAsync("dlh001", 6);

        using var head = await _host.Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Head, $"/download/{token}"));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, head.StatusCode);

        using var scope = _host.Scope();
        var db = _host.Service<NexusDbContext>(scope);
        Assert.Equal(0, (await db.DownloadGrants.AsNoTracking().FirstAsync(g => g.TokenHash == Hash(token))).UsedCount);
    }

    [Fact]
    public async Task The_status_endpoint_answers_without_counting()
    {
        var token = await GrantAsync("dls001", 7);

        for (var i = 0; i < 10; i++)
        {
            using var status = await _host.Client.GetAsync($"/api/downloads/{token}/status");
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            Assert.True(status.Headers.CacheControl?.NoStore);

            var body = await status.Content.ReadAsStringAsync();
            Assert.Contains("\"left\":3", body, StringComparison.Ordinal);
            // The token may have been forwarded; the holder is not necessarily the buyer.
            Assert.DoesNotContain("@example.com", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Nadia", body, StringComparison.Ordinal);
        }

        using var scope = _host.Scope();
        var db = _host.Service<NexusDbContext>(scope);
        Assert.Equal(0, (await db.DownloadGrants.AsNoTracking().FirstAsync(g => g.TokenHash == Hash(token))).UsedCount);
    }

    [Fact]
    public async Task The_status_endpoint_does_not_confirm_an_unknown_token()
    {
        using var response = await _host.Client.GetAsync(
            "/api/downloads/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/status");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The product lives outside the web root and nothing routes there. Both halves of
    /// that are defaults somebody could change, so it is asserted rather than assumed.
    /// </summary>
    [Theory]
    [InlineData("/App_Data/library/1/x.pdf")]
    [InlineData("/App_Data/dpkeys/key.xml")]
    [InlineData("/app_data/library/1/x.pdf")]
    public async Task App_data_is_not_reachable_over_http(string path)
    {
        using var response = await _host.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// A three-hundred-page book is stamped once per grant, not once per download. The
    /// cache file's write time is the evidence.
    /// </summary>
    [Fact]
    public async Task The_stamped_copy_is_built_once_per_grant()
    {
        var token = await GrantAsync("dlw001", 8);
        var stamped = Path.Combine(_host.LibraryRoot, "stamped");

        using (var first = await _host.Client.GetAsync($"/download/{token}")) { Assert.Equal(HttpStatusCode.OK, first.StatusCode); }

        var files = Directory.Exists(stamped)
            ? Directory.GetFiles(stamped, "*.pdf", SearchOption.AllDirectories)
            : [];

        // No font on this machine means no watermark and no cache entry — the book is
        // still delivered, which is the documented degradation. Only assert the cache
        // behaviour when stamping actually happened.
        if (files.Length == 0) return;

        var written = File.GetLastWriteTimeUtc(files[0]);

        using (var second = await _host.Client.GetAsync($"/download/{token}")) { Assert.Equal(HttpStatusCode.OK, second.StatusCode); }

        Assert.Single(Directory.GetFiles(stamped, "*.pdf", SearchOption.AllDirectories));
        Assert.Equal(written, File.GetLastWriteTimeUtc(files[0]));
    }

    private static string Hash(string token) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.ASCII.GetBytes(token))).ToLowerInvariant();
}
