using Microsoft.EntityFrameworkCore;
using Nexus.Application.Delivery;
using Nexus.Infrastructure.Delivery;
using Nexus.Infrastructure.Persistence;

namespace Nexus.IntegrationTests;

/// <summary>
/// The watermark. PDFsharp 6 ships no fonts, so the first thing worth proving is that a
/// font was found at all — without one every stamping test passes on a developer's
/// Windows box and quietly does nothing on a build agent.
/// </summary>
[Collection(NexusCollection.Name)]
public sealed class DeliveryStampingTests(NexusApp app) : IDisposable
{
    private readonly DeliveryHost _host = new(app);

    public void Dispose() => _host.Dispose();

    [Fact]
    public void A_font_resolver_is_registered_or_the_reason_is_visible()
    {
        using var scope = _host.Scope();
        var fonts = _host.Service<StampFontResolver>(scope);

        Assert.True(
            fonts.HasFont,
            "No TrueType font was found, so downloads are served unstamped. " +
            "Set Nexus:Library:StampFontPath, or install a font on this machine.");
    }

    /// <summary>
    /// Three concurrent first downloads of one grant must produce one stamped file, not
    /// three racing for the same name — and every one of them must still be served.
    /// </summary>
    [Fact]
    public async Task Concurrent_first_downloads_of_one_grant_produce_one_stamp()
    {
        var (order, _) = await _host.PaidOrderAsync("stm001", 20);

        IssuedGrant issued;
        using (var scope = _host.Scope())
        {
            issued = (await _host.Service<IDeliveryService>(scope).IssueGrantsForOrderAsync(order.Id)).Single();

            var db = _host.Service<NexusDbContext>(scope);
            var grant = await db.DownloadGrants.FirstAsync(g => g.Id == issued.Grant.Id);
            grant.MaxDownloads = 5;
            await db.SaveChangesAsync();
        }

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 3).Select(_ => _host.Client.GetAsync($"/download/{issued.Token}")));

        Assert.All(responses, r => Assert.Equal(System.Net.HttpStatusCode.OK, r.StatusCode));

        var stamped = Directory.GetFiles(
            Path.Combine(_host.LibraryRoot, "stamped"), "*.pdf", SearchOption.AllDirectories);

        Assert.Single(stamped);
        Assert.Contains($"{issued.Grant.Id}-", Path.GetFileName(stamped[0]), StringComparison.Ordinal);
        // The token is a bearer credential and must never appear in a filename.
        Assert.DoesNotContain(issued.Token, stamped[0], StringComparison.Ordinal);

        foreach (var response in responses) response.Dispose();
    }

    /// <summary>
    /// A saturated PDF worker refuses with a wait — and charges nobody for it. The
    /// bytes are produced before the counter moves, so an attempt that produced nothing
    /// cannot have spent an allowance.
    /// </summary>
    [Fact]
    public async Task A_saturated_worker_refuses_without_spending_a_download()
    {
        var (order, _) = await _host.PaidOrderAsync("stm003", 16);

        using var scope = _host.Scope();
        var issued = (await _host.Service<IDeliveryService>(scope).IssueGrantsForOrderAsync(order.Id)).Single();
        var concurrency = _host.Service<PdfConcurrency>(scope);

        Assert.True(await concurrency.TryEnterAsync(TimeSpan.FromSeconds(5), default));
        try
        {
            using var response = await _host.Client.GetAsync($"/download/{issued.Token}");

            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("10", response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString("0"));
        }
        finally
        {
            concurrency.Exit();
        }

        var db = _host.Service<NexusDbContext>(scope);
        var grant = await db.DownloadGrants.AsNoTracking().FirstAsync(g => g.Id == issued.Grant.Id);

        Assert.Equal(0, grant.UsedCount);
        Assert.Equal(Nexus.Domain.GrantState.Active, grant.State);
    }

    /// <summary>
    /// The stamped copy is a real PDF, is bigger than nothing, and is not the source —
    /// otherwise "stamping" could be a silent no-op nobody notices until an author asks.
    /// </summary>
    [Fact]
    public async Task A_stamped_copy_differs_from_the_source()
    {
        var (order, book) = await _host.PaidOrderAsync("stm002", 17);

        using var scope = _host.Scope();
        var issued = (await _host.Service<IDeliveryService>(scope).IssueGrantsForOrderAsync(order.Id)).Single();
        var library = _host.Service<IPdfLibrary>(scope);

        using var response = await _host.Client.GetAsync($"/download/{issued.Token}");
        var served = await response.Content.ReadAsByteArrayAsync();

        var db = _host.Service<NexusDbContext>(scope);
        var source = await File.ReadAllBytesAsync(
            library.ResolvePath((await db.Books.AsNoTracking().FirstAsync(b => b.Id == book.Id)).PdfStorageKey!)!);

        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(served[..5]), StringComparison.Ordinal);
        Assert.NotEqual(source.Length, served.Length);
    }
}
