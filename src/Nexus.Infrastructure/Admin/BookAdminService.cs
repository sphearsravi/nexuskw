using Microsoft.EntityFrameworkCore;
using Nexus.Application.Catalogue;
using Nexus.Application.Common;
using Nexus.Application.Delivery;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Delivery;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Admin;

/// <summary>
/// The catalogue, as the operator edits it.
/// <para>
/// This is the only write path to <see cref="Book"/>, and it is the reason the storefront
/// can trust <c>IsPublished</c>: publishing goes through <see cref="Book.PublishBlocker"/>
/// and nowhere else, so a fileless or placeholder title cannot reach the shop window by
/// any route an operator has.
/// </para>
/// </summary>
public sealed class BookAdminService(
    NexusDbContext db,
    IPdfLibrary library,
    IAuditLog audit,
    IAdminActorAccessor actors)
{
    public async Task<IReadOnlyList<AdminBookRow>> ListAsync(
        string? q, string? categorySlug, string? status, CancellationToken ct = default)
    {
        // Unpublished titles are the ones the operator most needs to see, so this reads
        // Books directly rather than through ICatalogueService, which filters them out
        // by design (CAT-02).
        var query = db.Books.AsNoTracking().Include(b => b.Category).AsQueryable();

        if (!string.IsNullOrWhiteSpace(categorySlug))
            query = query.Where(b => b.CategorySlug == categorySlug);

        query = status?.ToLowerInvariant() switch
        {
            "published" => query.Where(b => b.IsPublished),
            "draft" => query.Where(b => !b.IsPublished),
            "nofile" => query.Where(b => b.PdfStorageKey == null || b.PdfIsPlaceholder),
            _ => query
        };

        var books = await query
            .OrderByDescending(b => b.AddedToStoreAt)
            .ThenByDescending(b => b.Id)
            .ToListAsync(ct);

        // Folded in memory, exactly as the storefront search is: a SQL LIKE compares the
        // folded needle against an unfolded column, so "المحفظه" would stop matching
        // "المحفظة" in the admin while still matching in the shop.
        var needle = ArabicSearch.Fold(q);
        if (needle.Length > 0)
        {
            books = books.Where(b =>
                ArabicSearch.Contains(b.TitleEn, needle) ||
                ArabicSearch.Contains(b.AuthorEn, needle) ||
                ArabicSearch.Contains(b.TitleAr, needle) ||
                ArabicSearch.Contains(b.AuthorAr, needle) ||
                ArabicSearch.Contains(b.Isbn, needle)).ToList();
        }

        return books.Select(Row).ToList();
    }

    public async Task<AdminBookRow?> GetAsync(int id, CancellationToken ct = default)
    {
        var book = await db.Books.AsNoTracking().Include(b => b.Category)
            .FirstOrDefaultAsync(b => b.Id == id, ct);

        return book is null ? null : Row(book);
    }

    public async Task<(AdminBookRow? Book, IReadOnlyList<FieldError> Errors)> CreateAsync(
        BookWrite write, CancellationToken ct = default)
    {
        var normalised = BookValidation.Normalise(write);
        var slugs = await SlugsAsync(ct);

        var errors = BookValidation.Check(normalised, slugs);
        if (errors.Count > 0) return (null, errors);

        var now = DateTimeOffset.UtcNow;
        var book = new Book
        {
            // Allocated here: Book.Id is ValueGeneratedNever so seeded ids survive a reseed.
            Id = (await db.Books.MaxAsync(b => (int?)b.Id, ct) ?? 0) + 1,
            CategorySlug = normalised.CategorySlug,
            PriceKwd = normalised.PriceKwd,
            Pages = normalised.Pages,
            Year = normalised.Year,
            Isbn = normalised.Isbn,
            TitleEn = normalised.TitleEn,
            AuthorEn = normalised.AuthorEn,
            DescriptionEn = normalised.DescriptionEn,
            TitleAr = normalised.TitleAr,
            AuthorAr = normalised.AuthorAr,
            DescriptionAr = normalised.DescriptionAr,
            ContentLanguages = BookValidation.ParseLanguages(normalised.ContentLanguages),
            CoverMotif = normalised.CoverMotif,
            // Never published on creation: there is no file yet, and the gate would refuse.
            IsPublished = false,
            AddedToStoreAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Books.Add(book);
        await audit.RecordAsync("Book", book.Id, "book.create", null, Snapshot(book), ct);
        await db.SaveChangesAsync(ct);

        return (await GetAsync(book.Id, ct), []);
    }

    public async Task<(AdminBookRow? Book, IReadOnlyList<FieldError> Errors, AdminProblem? Problem)> UpdateAsync(
        int id, BookWrite write, CancellationToken ct = default)
    {
        var book = await db.Books.Include(b => b.Category).FirstOrDefaultAsync(b => b.Id == id, ct);
        if (book is null) return (null, [], new AdminProblem("book_not_found"));

        var normalised = BookValidation.Normalise(write);
        var slugs = await SlugsAsync(ct);

        var errors = BookValidation.Check(normalised, slugs);
        if (errors.Count > 0) return (null, errors, null);

        var before = Snapshot(book);

        book.CategorySlug = normalised.CategorySlug;
        book.PriceKwd = normalised.PriceKwd;
        book.Pages = normalised.Pages;
        book.Year = normalised.Year;
        book.Isbn = normalised.Isbn;
        book.TitleEn = normalised.TitleEn;
        book.AuthorEn = normalised.AuthorEn;
        book.DescriptionEn = normalised.DescriptionEn;
        book.TitleAr = normalised.TitleAr;
        book.AuthorAr = normalised.AuthorAr;
        book.DescriptionAr = normalised.DescriptionAr;
        book.ContentLanguages = BookValidation.ParseLanguages(normalised.ContentLanguages);
        book.CoverMotif = normalised.CoverMotif;
        book.UpdatedAt = DateTimeOffset.UtcNow;

        /* An edit that breaks the record of a live title takes it off sale rather than
         * leaving the storefront showing a book it can no longer describe in Arabic. The
         * Category navigation is reloaded because ContentBlocker reads IsActive off it. */
        await db.Entry(book).Reference(b => b.Category).LoadAsync(ct);
        var unpublished = false;
        if (book.IsPublished && book.PublishBlocker() is not null)
        {
            book.IsPublished = false;
            unpublished = true;
        }

        await audit.RecordAsync("Book", book.Id, unpublished ? "book.update.unpublished" : "book.update",
            before, Snapshot(book), ct);
        await db.SaveChangesAsync(ct);

        return (await GetAsync(id, ct), [], null);
    }

    /// <summary>
    /// The gate. <see cref="Book.PublishBlocker"/> decides and its message is handed back
    /// verbatim, so there is exactly one statement of what makes a title sellable.
    /// </summary>
    public async Task<(AdminBookRow? Book, AdminProblem? Problem)> PublishAsync(
        int id, CancellationToken ct = default)
    {
        var book = await db.Books.Include(b => b.Category).FirstOrDefaultAsync(b => b.Id == id, ct);
        if (book is null) return (null, new AdminProblem("book_not_found"));

        var blocker = book.PublishBlocker();
        if (blocker is not null) return (null, new AdminProblem("publish_blocked", blocker));

        if (book.IsPublished) return (await GetAsync(id, ct), null);

        book.IsPublished = true;
        book.UpdatedAt = DateTimeOffset.UtcNow;

        await audit.RecordAsync("Book", book.Id, "book.publish",
            new { IsPublished = false }, new { IsPublished = true }, ct);
        await db.SaveChangesAsync(ct);

        return (await GetAsync(id, ct), null);
    }

    public async Task<(AdminBookRow? Book, AdminProblem? Problem)> UnpublishAsync(
        int id, CancellationToken ct = default)
    {
        var book = await db.Books.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (book is null) return (null, new AdminProblem("book_not_found"));

        if (!book.IsPublished) return (await GetAsync(id, ct), null);

        book.IsPublished = false;
        book.UpdatedAt = DateTimeOffset.UtcNow;

        await audit.RecordAsync("Book", book.Id, "book.unpublish",
            new { IsPublished = true }, new { IsPublished = false }, ct);
        await db.SaveChangesAsync(ct);

        return (await GetAsync(id, ct), null);
    }

    /// <summary>
    /// Deleting is refused once a title has been sold. The order history is an accounting
    /// record and the grants still have to resolve to a file — "nobody can download it any
    /// more" is unpublishing, which is a different and reversible thing.
    /// </summary>
    public async Task<AdminProblem?> DeleteAsync(int id, CancellationToken ct = default)
    {
        var book = await db.Books.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (book is null) return new AdminProblem("book_not_found");

        var sold = await db.OrderItems.AnyAsync(i => i.BookId == id, ct);
        if (sold) return new AdminProblem("book_has_orders");

        var key = book.PdfStorageKey;

        db.Books.Remove(book);
        await audit.RecordAsync("Book", book.Id, "book.delete", Snapshot(book), null, ct);
        await db.SaveChangesAsync(ct);

        // After the commit, never inside it: a file delete that throws must not be able to
        // roll back the row that said the book is gone.
        if (key is not null) await library.DeleteAsync(key, ct);

        return null;
    }

    /// <summary>
    /// Stores the file through <see cref="IPdfLibrary"/>, which validates by magic bytes
    /// and structure rather than by what the browser called it.
    /// </summary>
    public async Task<(AdminBookRow? Book, AdminProblem? Problem)> UploadPdfAsync(
        int id, Stream content, string originalFileName, CancellationToken ct = default)
    {
        var book = await db.Books.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (book is null) return (null, new AdminProblem("book_not_found"));

        var replaced = book.PdfStorageKey;

        string key;
        string sha;
        long size;
        int pages;
        try
        {
            (key, sha, size, pages) = await library.StoreAsync(id, content, originalFileName, ct);
        }
        catch (PdfRejectedException rejected)
        {
            return (null, new AdminProblem(rejected.Code));
        }

        var before = Snapshot(book);

        book.PdfStorageKey = key;
        book.PdfSha256 = sha;
        book.PdfSizeBytes = size;
        book.PdfPageCount = pages;
        book.PdfOriginalFileName = Path.GetFileName(originalFileName);
        book.PdfUploadedAt = DateTimeOffset.UtcNow;
        book.PdfUploadedByAdminUserId = actors.Current.Id;
        // The real file lands: whatever the seeder wrote is no longer what is being sold.
        book.PdfIsPlaceholder = false;
        book.UpdatedAt = DateTimeOffset.UtcNow;

        await audit.RecordAsync("Book", book.Id, "book.pdf.upload", before, Snapshot(book), ct);
        await db.SaveChangesAsync(ct);

        // Only once the new key is committed, and only when it is genuinely a different
        // file: deleting first would leave a published title with nothing to deliver if
        // the save failed.
        if (replaced is not null && replaced != key) await library.DeleteAsync(replaced, ct);

        return (await GetAsync(id, ct), null);
    }

    private async Task<IReadOnlySet<string>> SlugsAsync(CancellationToken ct) =>
        (await db.Categories.AsNoTracking().Select(c => c.Slug).ToListAsync(ct))
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>What the audit trail keeps. No file paths, no hashes: neither answers a question later.</summary>
    private static object Snapshot(Book b) => new
    {
        b.CategorySlug,
        b.PriceKwd,
        b.Pages,
        b.Year,
        b.Isbn,
        b.TitleEn,
        b.AuthorEn,
        b.TitleAr,
        b.AuthorAr,
        b.IsPublished,
        HasPdf = b.PdfStorageKey is not null,
        b.PdfIsPlaceholder,
        b.CoverMotif,
        ContentLanguages = b.ContentLanguages.ToString()
    };

    private static AdminBookRow Row(Book b) => new(
        b.Id,
        b.CategorySlug,
        b.PriceKwd,
        b.Pages,
        b.Year,
        b.SalesCount,
        new LocalisedText(b.TitleEn, b.AuthorEn, b.DescriptionEn),
        new LocalisedText(b.TitleAr, b.AuthorAr, b.DescriptionAr),
        b.CoverMotif ?? b.CategorySlug,
        b.Isbn,
        b.ContentLanguages.ToString(),
        b.IsPublished,
        b.PdfStorageKey is not null,
        b.PdfIsPlaceholder,
        b.PdfSizeBytes,
        b.PdfPageCount,
        b.PdfOriginalFileName,
        b.PdfUploadedAt,
        b.PublishBlocker(),
        b.AddedToStoreAt,
        b.UpdatedAt);
}
