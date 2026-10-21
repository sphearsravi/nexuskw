using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Nexus.Application.Common;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Admin;

/// <summary>What an import did, or would have done. Always returned, accepted or not.</summary>
public sealed record ImportReport(
    bool Accepted,
    bool DryRun,
    int RowsRead,
    int Created,
    IReadOnlyList<FieldError> Errors,
    IReadOnlyList<int> CreatedBookIds);

/// <summary>
/// Bulk catalogue import from CSV.
/// <para>
/// <b>Nothing is written until every row has passed.</b> A partial import is the worst
/// outcome available: the operator cannot tell which half landed, re-running duplicates
/// the good rows, and the file they were working from no longer describes the catalogue.
/// All-or-nothing turns a bad file into a corrected file instead of a cleanup job.
/// </para>
/// <para>
/// Imported titles are created <b>unpublished and fileless</b>. The PDF is uploaded per
/// title afterwards and the publish gate refuses until it exists — a bulk import must not
/// become a way around the one rule that stops an undeliverable book going on sale.
/// </para>
/// </summary>
public sealed class CatalogueImportService(NexusDbContext db, IAuditLog audit)
{
    /// <summary>Both languages per row. Column order is free; the header names are what match.</summary>
    public static readonly string[] Columns =
    [
        "category_slug", "price_kwd", "pages", "year", "isbn",
        "title_en", "author_en", "description_en",
        "title_ar", "author_ar", "description_ar",
        "content_languages", "cover_motif"
    ];

    private static readonly string[] RequiredColumns =
    [
        "category_slug", "price_kwd", "pages", "year",
        "title_en", "author_en", "description_en",
        "title_ar", "author_ar", "description_ar"
    ];

    /// <summary>
    /// A starting file for the operator, Arabic columns filled in so the shape of a real
    /// row is obvious. Written as escapes rather than literals so no editor or transfer
    /// can quietly re-encode the sample and teach the operator the wrong thing.
    /// </summary>
    public static string TemplateCsv()
    {
        const string titleAr = "المحفظة الصبور";
        const string authorAr = "نادية الصباح";
        const string descriptionAr = "دليل طويل الأمد.";

        return string.Join(",", Columns) + "\r\n" +
               "investing,6.500,220,2025,,The Patient Portfolio,Nadia Al-Sabah," +
               "\"A long-horizon guide, written for Kuwait.\"," +
               titleAr + "," + authorAr + "," + descriptionAr + ",ArabicAndEnglish,\r\n";
    }

    public async Task<ImportReport> ImportAsync(string csv, bool dryRun, CancellationToken ct = default)
    {
        var rows = CsvReader.Parse(csv);
        if (rows.Count == 0) return Refused([new FieldError(0, "file", "empty")], dryRun, 0);

        var header = rows[0].Fields.Select(f => f.Trim().ToLowerInvariant()).ToList();
        var headerErrors = CheckHeader(header);
        if (headerErrors.Count > 0) return Refused(headerErrors, dryRun, 0);

        var index = header
            .Select((name, at) => (name, at))
            .Where(x => Columns.Contains(x.name))
            .ToDictionary(x => x.name, x => x.at, StringComparer.Ordinal);

        var slugs = (await db.Categories.AsNoTracking().Select(c => c.Slug).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var errors = new List<FieldError>();
        var writes = new List<(int Line, BookWrite Write)>();

        foreach (var row in rows.Skip(1))
        {
            var line = row.Line;

            if (row.Fields.Count < header.Count)
            {
                errors.Add(new FieldError(line, "row", "too_few_columns",
                    row.Fields.Count.ToString(CultureInfo.InvariantCulture)));
                continue;
            }

            string Cell(string column) =>
                index.TryGetValue(column, out var at) && at < row.Fields.Count ? row.Fields[at].Trim() : "";

            var priceText = Cell("price_kwd");
            var pagesText = Cell("pages");
            var yearText = Cell("year");

            // InvariantCulture, not the server's: a comma decimal separator on an Arabic
            // machine would read 6,500 as six thousand five hundred dinars.
            if (!decimal.TryParse(priceText, NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
            {
                errors.Add(new FieldError(line, "priceKwd", "not_a_number", priceText));
                price = 0m;
            }

            if (!int.TryParse(pagesText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pages))
            {
                errors.Add(new FieldError(line, "pages", "not_a_number", pagesText));
                pages = 0;
            }

            if (!int.TryParse(yearText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
            {
                errors.Add(new FieldError(line, "year", "not_a_number", yearText));
                year = 0;
            }

            var write = BookValidation.Normalise(new BookWrite(
                Cell("category_slug"), price, pages, year, Cell("isbn"),
                Cell("title_en"), Cell("author_en"), Cell("description_en"),
                Cell("title_ar"), Cell("author_ar"), Cell("description_ar"),
                Cell("content_languages"), Cell("cover_motif")));

            errors.AddRange(BookValidation.Check(write, slugs, line));
            writes.Add((line, write));
        }

        errors.AddRange(await DuplicatesAsync(writes, ct));

        if (errors.Count > 0) return Refused(errors, dryRun, rows.Count - 1);
        if (dryRun) return new ImportReport(true, true, writes.Count, 0, [], []);

        var created = await WriteAsync(writes.Select(w => w.Write).ToList(), ct);
        return new ImportReport(true, false, writes.Count, created.Count, [], created);
    }

    private static List<FieldError> CheckHeader(List<string> header)
    {
        var errors = new List<FieldError>();

        foreach (var column in RequiredColumns.Where(c => !header.Contains(c)))
            errors.Add(new FieldError(1, column, "missing_column"));

        foreach (var duplicate in header.GroupBy(h => h).Where(g => g.Key.Length > 0 && g.Count() > 1))
            errors.Add(new FieldError(1, duplicate.Key, "duplicate_column"));

        return errors;
    }

    /// <summary>
    /// Clashes inside the file and against the catalogue. Both matter: an import run twice
    /// by a nervous operator must not silently double the shelf.
    /// </summary>
    private async Task<List<FieldError>> DuplicatesAsync(
        List<(int Line, BookWrite Write)> writes, CancellationToken ct)
    {
        var errors = new List<FieldError>();

        foreach (var clash in writes
                     .Where(w => w.Write.Isbn is { Length: > 0 })
                     .GroupBy(w => w.Write.Isbn!, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            foreach (var row in clash.Skip(1))
                errors.Add(new FieldError(row.Line, "isbn", "duplicate_in_file", clash.Key));
        }

        foreach (var clash in writes
                     .GroupBy(w => w.Write.TitleEn, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Key.Length > 0 && g.Count() > 1))
        {
            foreach (var row in clash.Skip(1))
                errors.Add(new FieldError(row.Line, "titleEn", "duplicate_in_file", clash.Key));
        }

        var isbns = writes
            .Select(w => w.Write.Isbn)
            .Where(i => i is { Length: > 0 })
            .Select(i => i!)
            .ToList();

        if (isbns.Count > 0)
        {
            var taken = await db.Books.AsNoTracking()
                .Where(b => b.Isbn != null && isbns.Contains(b.Isbn))
                .Select(b => b.Isbn!)
                .ToListAsync(ct);

            foreach (var row in writes.Where(w =>
                         w.Write.Isbn is { Length: > 0 } && taken.Contains(w.Write.Isbn)))
                errors.Add(new FieldError(row.Line, "isbn", "already_in_catalogue", row.Write.Isbn));
        }

        var titles = writes.Select(w => w.Write.TitleEn).Where(t => t.Length > 0).Distinct().ToList();
        if (titles.Count > 0)
        {
            var existing = await db.Books.AsNoTracking()
                .Where(b => titles.Contains(b.TitleEn))
                .Select(b => new { b.TitleEn, b.AuthorEn })
                .ToListAsync(ct);

            foreach (var row in writes.Where(w => existing.Any(e =>
                         string.Equals(e.TitleEn, w.Write.TitleEn, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(e.AuthorEn, w.Write.AuthorEn, StringComparison.OrdinalIgnoreCase))))
                errors.Add(new FieldError(row.Line, "titleEn", "already_in_catalogue", row.Write.TitleEn));
        }

        return errors;
    }

    private async Task<List<int>> WriteAsync(List<BookWrite> writes, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // Book.Id is ValueGeneratedNever — CAT-05 keeps seeded ids stable across a reseed —
        // so ids are allocated here rather than by the database. The whole block is taken
        // at once and the insert is a single SaveChanges, so a concurrent import collides
        // on the primary key and rolls back rather than interleaving.
        var next = (await db.Books.MaxAsync(b => (int?)b.Id, ct) ?? 0) + 1;
        var ids = new List<int>(writes.Count);

        foreach (var write in writes)
        {
            var book = new Book
            {
                Id = next++,
                CategorySlug = write.CategorySlug,
                PriceKwd = write.PriceKwd,
                Pages = write.Pages,
                Year = write.Year,
                Isbn = write.Isbn,
                TitleEn = write.TitleEn,
                AuthorEn = write.AuthorEn,
                DescriptionEn = write.DescriptionEn,
                TitleAr = write.TitleAr,
                AuthorAr = write.AuthorAr,
                DescriptionAr = write.DescriptionAr,
                ContentLanguages = BookValidation.ParseLanguages(write.ContentLanguages),
                CoverMotif = write.CoverMotif,
                IsPublished = false,
                AddedToStoreAt = now,
                CreatedAt = now,
                UpdatedAt = now
            };

            db.Books.Add(book);
            ids.Add(book.Id);

            await audit.RecordAsync("Book", book.Id, "book.import", null, new
            {
                book.CategorySlug,
                book.PriceKwd,
                book.TitleEn,
                book.TitleAr
            }, ct);
        }

        await db.SaveChangesAsync(ct);
        return ids;
    }

    private static ImportReport Refused(List<FieldError> errors, bool dryRun, int rowsRead) =>
        new(false, dryRun, rowsRead, 0, errors, []);
}
