using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexus.Application.Common;
using Nexus.Application.Delivery;
using Nexus.Infrastructure.Persistence;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Nexus.Infrastructure.Delivery;

/// <summary>
/// The PDF store. Files live under <c>App_Data</c>, outside the web root, and are named
/// from their own SHA-256 — so no route, no client filename and no operator text ever
/// reaches a path.
/// </summary>
public sealed class PdfLibrary(
    NexusDbContext db,
    LibraryPaths paths,
    ISettingsReader settings,
    PdfConcurrency concurrency,
    ILogger<PdfLibrary> log) : IPdfLibrary
{
    private const long DefaultMaxUploadBytes = 67_108_864;   // 64 MB
    private const long MinUploadBytes = 1_048_576;           // 1 MB
    private const long MaxUploadBytes = 268_435_456;         // 256 MB

    public async Task<(string StorageKey, string Sha256, long SizeBytes, int PageCount)> StoreAsync(
        int bookId, Stream content, string originalFileName, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bookId, 1);

        var cap = await settings.GetLongAsync(
            SettingKeys.MaxUploadBytes, DefaultMaxUploadBytes, MinUploadBytes, MaxUploadBytes, ct);

        paths.EnsureDirectories();
        var quarantine = Path.Combine(paths.Incoming, $"{Guid.NewGuid():N}.part");

        try
        {
            var (sha, size) = await WriteAndHashAsync(content, quarantine, cap, ct);

            var shape = await PdfMagicBytes.InspectFileAsync(quarantine, ct);
            if (shape != PdfShapeVerdict.Ok)
                throw new PdfRejectedException(PdfMagicBytes.Code(shape), $"structure check: {shape}");

            var pageCount = await ParsePageCountAsync(quarantine, ct);
            var key = Promote(bookId, sha, quarantine);

            log.LogInformation(
                "Stored PDF for book {BookId}: {Size} bytes, {Pages} pages, sha {Sha}",
                bookId, size, pageCount, sha);

            return (key, sha, size, pageCount);
        }
        finally
        {
            // The quarantine file is disposable by definition; a failure to tidy up must
            // not turn a rejected upload into a 500.
            TryDelete(quarantine);
        }
    }

    public string? ResolvePath(string storageKey)
    {
        var full = paths.ResolveLibrary(storageKey);
        return full is not null && File.Exists(full) ? full : null;
    }

    public Task DeleteAsync(string storageKey, CancellationToken ct = default)
    {
        var full = paths.ResolveLibrary(storageKey);
        if (full is not null) TryDelete(full);
        return Task.CompletedTask;
    }

    /// <summary>
    /// A one-page stub so development has something to serve, and
    /// <see cref="Nexus.Domain.Entities.Book.PdfIsPlaceholder"/> set so the publish gate
    /// still refuses to sell it. "Production seeds unpublished" is not the same promise
    /// as "production has no placeholder files".
    /// </summary>
    public async Task<(string StorageKey, string Sha256, long SizeBytes, int PageCount)> WritePlaceholderAsync(
        int bookId, CancellationToken ct = default)
    {
        var book = await db.Books.FirstOrDefaultAsync(b => b.Id == bookId, ct)
            ?? throw new InvalidOperationException($"Book {bookId} does not exist.");

        paths.EnsureDirectories();
        var bytes = PlaceholderPdf.Bytes();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var key = LibraryPaths.KeyFor(bookId, sha);
        var full = paths.ResolveLibrary(key)!;

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (!File.Exists(full)) await File.WriteAllBytesAsync(full, bytes, ct);

        book.PdfStorageKey = key;
        book.PdfSha256 = sha;
        book.PdfSizeBytes = bytes.LongLength;
        book.PdfPageCount = 1;
        book.PdfOriginalFileName = "placeholder.pdf";
        book.PdfUploadedAt = DateTimeOffset.UtcNow;
        book.PdfIsPlaceholder = true;
        book.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return (key, sha, bytes.LongLength, 1);
    }

    /// <summary>
    /// One pass over the stream: written to quarantine and hashed from the same buffer.
    /// A server with 512 MB shared across six apps cannot afford
    /// <c>CopyToAsync(new MemoryStream())</c>, and that is the line most upload code
    /// actually contains.
    /// </summary>
    private static async Task<(string Sha256, long Size)> WriteAndHashAsync(
        Stream content, string quarantine, long cap, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81_920];
        long written = 0;

        await using (var file = new FileStream(
            quarantine, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65_536,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                written += read;
                // Aborted the moment it goes over, not after the whole file has landed:
                // the cap exists to protect the disk, and a cap you only check at the end
                // has already let the damage happen.
                if (written > cap)
                    throw new PdfRejectedException("pdf_too_large", $"over {cap} bytes");

                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                hash.AppendData(buffer, 0, read);
            }
        }

        if (written == 0) throw new PdfRejectedException("pdf_bad_magic", "empty upload");
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), written);
    }

    private async Task<int> ParsePageCountAsync(string path, CancellationToken ct)
    {
        // Same whole-document managed load as stamping, so the same gate (C-upload).
        if (!await concurrency.TryEnterAsync(TimeSpan.FromSeconds(30), ct))
            throw new PdfRejectedException("busy", "the PDF worker is saturated");

        try
        {
            using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            return doc.PageCount;
        }
        catch (PdfReaderException ex) when (IsPasswordProblem(ex))
        {
            // Refused outright: we cannot stamp it, and if we could, the buyer would
            // still be asked for a password we do not have.
            throw new PdfRejectedException("pdf_encrypted", ex.Message);
        }
        catch (Exception ex) when (ex is not PdfRejectedException and not OperationCanceledException)
        {
            throw new PdfRejectedException("pdf_unparseable", ex.Message);
        }
        finally
        {
            concurrency.Exit();
        }
    }

    private static bool IsPasswordProblem(Exception ex) =>
        ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase);

    private string Promote(int bookId, string sha, string quarantine)
    {
        var key = LibraryPaths.KeyFor(bookId, sha);
        var destination = paths.ResolveLibrary(key)
            ?? throw new InvalidOperationException($"Refusing a storage key that escapes the library: {key}");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        // Content-addressed: if the destination exists the bytes are identical by
        // definition, so re-uploading the same file is a no-op rather than a conflict.
        if (!File.Exists(destination)) File.Move(quarantine, destination);
        return key;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException ex)
        {
            log.LogWarning(ex, "Could not delete {Path}; the nightly sweep will.", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            log.LogWarning(ex, "Could not delete {Path}; the nightly sweep will.", path);
        }
    }
}

/// <summary>
/// The generated stub. Deliberately textless: drawing a string needs a font resolver,
/// and a seeder that fails on a machine with no fonts is a seeder that fails in CI.
/// </summary>
internal static class PlaceholderPdf
{
    public static byte[] Bytes()
    {
        using var doc = new PdfDocument();
        doc.Info.Title = "Nexus placeholder";
        doc.Info.Subject = "Generated stub — not the book.";
        doc.AddPage();

        using var buffer = new MemoryStream();
        doc.Save(buffer, closeStream: false);
        return buffer.ToArray();
    }
}
