using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexus.Application.Delivery;
using Nexus.Domain.Entities;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Nexus.Infrastructure.Delivery;

/// <summary>The PDF worker was saturated. Nobody's download count is charged for this.</summary>
public sealed class PdfBusyException() : Exception("The PDF worker is saturated.");

/// <summary>
/// Stamps the buyer's details into a copy of the book and caches that copy per grant, so
/// a three-hundred-page title is stamped once rather than once per download.
/// </summary>
public sealed class PdfSharpStamper : IPdfStamper
{
    private readonly LibraryPaths _paths;
    private readonly PdfConcurrency _concurrency;
    private readonly DeliveryOptions _options;
    private readonly StampFontResolver _fonts;
    private readonly ILogger<PdfSharpStamper> _log;

    /// <summary>
    /// One lock per grant in front of the process-wide gate: three concurrent first
    /// downloads of the same grant should produce one stamp and three served responses,
    /// not three stamps racing for the same filename.
    /// </summary>
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> PerGrant = new();

    public PdfSharpStamper(
        LibraryPaths paths,
        PdfConcurrency concurrency,
        IOptions<DeliveryOptions> options,
        StampFontResolver fonts,
        ILogger<PdfSharpStamper> log)
    {
        _paths = paths;
        _concurrency = concurrency;
        _options = options.Value;
        _fonts = fonts;
        _log = log;

        if (_fonts.HasFont)
        {
            // Global and set-once by PDFsharp's own design; assigning the same resolver
            // twice is harmless, assigning a second different one throws.
            GlobalFontSettings.FontResolver ??= _fonts;
        }
    }

    public async Task<string> StampedCopyAsync(
        DownloadGrant grant, string sourcePath, string buyerName, string buyerEmail,
        string orderNumber, CancellationToken ct = default)
    {
        var sourceSha = Path.GetFileNameWithoutExtension(sourcePath);
        var key = LibraryPaths.StampedKeyFor(grant.Id, sourceSha);
        var destination = _paths.ResolveStamped(key)
            ?? throw new InvalidOperationException($"Refusing a stamped key that escapes the cache: {key}");

        if (File.Exists(destination)) return destination;
        if (!_fonts.HasFont)
            throw new InvalidOperationException("No usable TrueType font for the watermark.");

        var perGrant = PerGrant.GetOrAdd(grant.Id, _ => new SemaphoreSlim(1, 1));
        await perGrant.WaitAsync(ct);
        try
        {
            if (File.Exists(destination)) return destination;

            var timeout = TimeSpan.FromSeconds(Math.Clamp(_options.StampQueueTimeoutSeconds, 1, 120));
            if (!await _concurrency.TryEnterAsync(timeout, ct)) throw new PdfBusyException();

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var footer = StampText.Footer(buyerName, buyerEmail, orderNumber, DateTimeOffset.UtcNow);
                Render(sourcePath, destination, footer, grant, orderNumber);
                return destination;
            }
            finally
            {
                _concurrency.Exit();
            }
        }
        finally
        {
            perGrant.Release();
            // The dictionary would otherwise grow one entry per grant ever downloaded.
            // Dropping it while another request is between GetOrAdd and WaitAsync only
            // costs a duplicated stamp, which Move already treats as success.
            if (perGrant.CurrentCount == 1) PerGrant.TryRemove(grant.Id, out _);
        }
    }

    public Task EvictAsync(DownloadGrant grant, CancellationToken ct = default)
    {
        // Housekeeping, never security: the token is refused by state, not by the
        // absence of a file. So a failure here is logged and forgotten.
        var prefix = $"{grant.Id}-";
        var bucket = Path.Combine(_paths.Stamped, $"{grant.Id % 256:x2}");

        try
        {
            if (Directory.Exists(bucket))
            {
                foreach (var file in Directory.EnumerateFiles(bucket, prefix + "*.pdf")) File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Stamped cache for grant {GrantId} not purged; the nightly pass will.", grant.Id);
        }

        return Task.CompletedTask;
    }

    private void Render(string sourcePath, string destination, string footer, DownloadGrant grant, string orderNumber)
    {
        // Stamp to a temp file and move it into place. Writing straight to the
        // destination means a pool recycle mid-stamp leaves a truncated PDF in the
        // cache, which is then served to that grant forever.
        var temp = destination + ".tmp-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            using (var doc = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Modify))
            {
                doc.Info.Keywords = $"nexus-order:{orderNumber} nexus-grant:{grant.Id}";
                var font = new XFont(StampFontResolver.FamilyName, 7);
                var brush = new XSolidBrush(XColor.FromArgb(90, 90, 90));

                foreach (var page in doc.Pages)
                {
                    using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                    // Width and height come from the graphics object, which has the
                    // page's /Rotate already applied — read from the MediaBox directly
                    // and a landscape or rotated page gets its stamp sideways.
                    var box = new XRect(12, gfx.PageSize.Height - 22, gfx.PageSize.Width - 24, 14);
                    gfx.DrawString(footer, font, brush, box, XStringFormats.Center);
                }

                doc.Save(temp);
            }

            Move(temp, destination);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch (IOException) { /* the sweep will */ }
            }
        }
    }

    private static void Move(string temp, string destination)
    {
        try
        {
            File.Move(temp, destination);
        }
        catch (IOException) when (File.Exists(destination))
        {
            // Another request won the race. The bytes are determined by the grant id and
            // the source hash, so whoever got there first wrote the same file.
        }
    }
}
