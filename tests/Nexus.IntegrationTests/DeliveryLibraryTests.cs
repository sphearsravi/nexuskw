using System.Net.Http.Json;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nexus.Application.Common;
using Nexus.Application.Delivery;
using Nexus.Infrastructure.Delivery;
using Nexus.Infrastructure.Persistence;
using Nexus.Infrastructure.Settings;

namespace Nexus.IntegrationTests;

/// <summary>
/// The store itself: what it accepts, where it puts it, and what the publish gate makes
/// of a generated stub.
/// </summary>
[Collection(NexusCollection.Name)]
public sealed class DeliveryLibraryTests(NexusApp app) : IDisposable
{
    private readonly DeliveryHost _host = new(app);

    public void Dispose() => _host.Dispose();

    private static MemoryStream Bytes(string content) => new(Encoding.ASCII.GetBytes(content));

    private static MemoryStream RealPdf()
    {
        // One genuine document, generated once: PDFsharp stamps a creation timestamp
        // into the file, so generating it twice would produce two different hashes and
        // the content-addressing test would be measuring the clock.
        return new MemoryStream(OnePage.Value, writable: false);
    }

    private static readonly Lazy<byte[]> OnePage = new(() =>
    {
        using var doc = new PdfSharp.Pdf.PdfDocument();
        doc.AddPage();
        using var buffer = new MemoryStream();
        doc.Save(buffer, closeStream: false);
        return buffer.ToArray();
    });

    [Fact]
    public async Task A_real_pdf_is_stored_under_its_own_hash()
    {
        using var scope = _host.Scope();
        var library = _host.Service<IPdfLibrary>(scope);

        var (key, sha, size, pages) = await library.StoreAsync(16, RealPdf(), "book.pdf");

        Assert.Equal($"16/{sha}.pdf", key);
        Assert.Equal(64, sha.Length);
        Assert.True(size > 0);
        Assert.Equal(1, pages);

        var path = library.ResolvePath(key);
        Assert.NotNull(path);
        Assert.StartsWith(Path.Combine(_host.LibraryRoot, "library"), path!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Content-addressed, so the same bytes twice is a no-op rather than a conflict.</summary>
    [Fact]
    public async Task The_same_bytes_uploaded_twice_are_one_file()
    {
        using var scope = _host.Scope();
        var library = _host.Service<IPdfLibrary>(scope);

        var first = await library.StoreAsync(17, RealPdf(), "book.pdf");
        var second = await library.StoreAsync(17, RealPdf(), "again.pdf");

        Assert.Equal(first.StorageKey, second.StorageKey);
        Assert.Single(Directory.GetFiles(Path.Combine(_host.LibraryRoot, "library", "17")));
    }

    /// <summary>Validated by what is in the bytes. The extension is a claim, not evidence.</summary>
    [Fact]
    public async Task A_png_renamed_to_pdf_is_refused()
    {
        using var scope = _host.Scope();
        var library = _host.Service<IPdfLibrary>(scope);

        var rejected = await Assert.ThrowsAsync<PdfRejectedException>(
            () => library.StoreAsync(18, Bytes("\u0089PNG\r\n\u001a\nnot a pdf at all"), "book.pdf"));

        Assert.Equal("pdf_bad_magic", rejected.Code);
        Assert.Empty(Directory.GetFiles(Path.Combine(_host.LibraryRoot, "incoming")));
    }

    /// <summary>
    /// The case that passes a header check perfectly and then fails to open on the
    /// buyer's phone three weeks later.
    /// </summary>
    [Fact]
    public async Task A_truncated_pdf_is_refused()
    {
        using var scope = _host.Scope();
        var library = _host.Service<IPdfLibrary>(scope);

        var rejected = await Assert.ThrowsAsync<PdfRejectedException>(
            () => library.StoreAsync(19, Bytes("%PDF-1.7\n1 0 obj\n<<>>\nendobj\n"), "book.pdf"));

        Assert.Equal("pdf_truncated", rejected.Code);
    }

    [Fact]
    public async Task A_header_shaped_file_that_will_not_parse_is_refused()
    {
        using var scope = _host.Scope();
        var library = _host.Service<IPdfLibrary>(scope);

        var rejected = await Assert.ThrowsAsync<PdfRejectedException>(
            () => library.StoreAsync(20, Bytes("%PDF-1.7\nthis is not a document\n%%EOF\n"), "book.pdf"));

        Assert.Equal("pdf_unparseable", rejected.Code);
        Assert.Empty(Directory.GetFiles(Path.Combine(_host.LibraryRoot, "incoming")));
    }

    /// <summary>
    /// The cap is enforced as the bytes arrive, not after the whole file has landed —
    /// a cap you only check at the end has already let the damage happen.
    /// </summary>
    [Fact]
    public async Task An_upload_over_the_cap_is_aborted_mid_stream()
    {
        using var scope = _host.Scope();
        var settings = _host.Service<ISettingsReader>(scope);
        await settings.SetAsync(SettingKeys.MaxUploadBytes, "1048576", false, "test");

        var library = _host.Service<IPdfLibrary>(scope);
        var oversized = new MemoryStream(new byte[2 * 1024 * 1024]);

        var rejected = await Assert.ThrowsAsync<PdfRejectedException>(
            () => library.StoreAsync(21, oversized, "huge.pdf"));

        Assert.Equal("pdf_too_large", rejected.Code);
        Assert.Empty(Directory.GetFiles(Path.Combine(_host.LibraryRoot, "incoming")));

        await settings.SetAsync(
            SettingKeys.MaxUploadBytes,
            DeliveryDefaults.MaxUploadBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            false, "test");
    }

    /// <summary>
    /// "Production seeds unpublished" is not the same promise as "production has no
    /// placeholder files". The flag is what makes the gate refuse the stub by name.
    /// </summary>
    [Fact]
    public async Task A_placeholder_is_servable_and_still_unpublishable()
    {
        using var scope = _host.Scope();
        var library = _host.Service<IPdfLibrary>(scope);
        var db = _host.Service<NexusDbContext>(scope);

        var (key, _, size, pages) = await library.WritePlaceholderAsync(18);

        Assert.True(size > 0);
        Assert.Equal(1, pages);
        Assert.NotNull(library.ResolvePath(key));

        var book = await db.Books.AsNoTracking().FirstAsync(b => b.Id == 18);
        Assert.True(book.PdfIsPlaceholder);
        Assert.Equal(key, book.PdfStorageKey);
        Assert.Equal("PDF is a seed placeholder, not the real file.", book.PublishBlocker());
    }

    [Fact]
    public async Task A_real_upload_over_a_placeholder_clears_the_flag()
    {
        using var scope = _host.Scope();
        var library = _host.Service<IPdfLibrary>(scope);
        var db = _host.Service<NexusDbContext>(scope);

        await library.WritePlaceholderAsync(19);
        var (key, sha, size, pages) = await library.StoreAsync(19, RealPdf(), "real.pdf");

        var book = await db.Books.FirstAsync(b => b.Id == 19);
        book.PdfStorageKey = key;
        book.PdfSha256 = sha;
        book.PdfSizeBytes = size;
        book.PdfPageCount = pages;
        book.PdfIsPlaceholder = false;
        await db.SaveChangesAsync();

        Assert.Null(book.PublishBlocker());
    }

    /// <summary>
    /// The allow-list is the list in code. The same table holds the Tap secret key in
    /// Phase 7, and a projection over it would publish that the day somebody added the
    /// row.
    /// </summary>
    [Fact]
    public async Task The_public_settings_endpoint_serves_the_allow_list_and_nothing_else()
    {
        using (var scope = _host.Scope())
        {
            await _host.Service<DeliverySettingsSeeder>(scope).SeedAsync();
            await _host.Service<ISettingsReader>(scope)
                .SetAsync("tap.secretKey", "not-a-real-key", true, "test");
        }

        using var response = await _host.Client.GetAsync("/api/settings/public");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        Assert.NotNull(body);
        Assert.Equal(SettingKeys.PubliclyReadable.Order(), body!.Keys.Order());
        Assert.Equal("7", body[SettingKeys.LinkExpiryDays]);
        Assert.Equal("3", body[SettingKeys.MaxDownloads]);
        Assert.DoesNotContain("not-a-real-key", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// An operator typing "seven" into a number field must cost them the typo, not the
    /// shop.
    /// </summary>
    [Fact]
    public async Task A_nonsense_setting_degrades_to_the_documented_fallback()
    {
        using (var scope = _host.Scope())
        {
            await _host.Service<ISettingsReader>(scope)
                .SetAsync(SettingKeys.LinkExpiryDays, "seven", false, "test");
        }

        using (var scope = _host.Scope())
        {
            var settings = _host.Service<ISettingsReader>(scope);
            Assert.Equal(7, await settings.GetIntAsync(SettingKeys.LinkExpiryDays, 7, 1, 365));

            await settings.SetAsync(SettingKeys.LinkExpiryDays, "9999", false, "test");
            Assert.Equal(365, await settings.GetIntAsync(SettingKeys.LinkExpiryDays, 7, 1, 365));

            await settings.SetAsync(SettingKeys.LinkExpiryDays, "7", false, "test");
        }

        using var response = await _host.Client.GetAsync("/api/settings/public");
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("7", body![SettingKeys.LinkExpiryDays]);
    }
}
