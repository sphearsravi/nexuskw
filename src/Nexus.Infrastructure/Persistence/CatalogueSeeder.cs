using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexus.Domain.Entities;

namespace Nexus.Infrastructure.Persistence;

/// <summary>
/// Loads the six categories and twenty titles that came from the demo.
/// <para>
/// Decision D4 (CAT-35): they are seeded into every environment so the
/// operator edits rather than retypes, but in production they arrive
/// <b>unpublished</b> — rights are not yet cleared per title, and nothing
/// unlicensed may ever be offered for sale.
/// </para>
/// Idempotent: existing rows are left alone, so a redeploy never overwrites
/// an operator's edits.
/// </summary>
public sealed class CatalogueSeeder(NexusDbContext db, ILogger<CatalogueSeeder> log)
{
    private sealed record SeedFile(List<SeedCategory> Categories, List<SeedBook> Books);

    private sealed record SeedCategory(string Slug, string NameEn, string NameAr, string Hue, int SortOrder, bool IsActive);

    private sealed record SeedBook(
        int Id, string CategorySlug, decimal PriceKwd, int Pages, int Year, int SalesCount,
        string TitleEn, string AuthorEn, string DescriptionEn,
        string TitleAr, string AuthorAr, string DescriptionAr);

    public async Task SeedAsync(bool publishBooks, CancellationToken ct = default)
    {
        var seed = Load();
        var now = DateTimeOffset.UtcNow;
        var added = 0;

        foreach (var c in seed.Categories)
        {
            if (await db.Categories.AnyAsync(x => x.Slug == c.Slug, ct)) continue;
            db.Categories.Add(new Category
            {
                Slug = c.Slug,
                NameEn = c.NameEn,
                NameAr = c.NameAr,
                Hue = c.Hue,
                SortOrder = c.SortOrder,
                IsActive = c.IsActive
            });
            added++;
        }
        if (added > 0) await db.SaveChangesAsync(ct);

        var seeded = 0;
        foreach (var b in seed.Books)
        {
            if (await db.Books.AnyAsync(x => x.Id == b.Id, ct)) continue;

            var book = new Book
            {
                Id = b.Id,
                CategorySlug = b.CategorySlug,
                PriceKwd = b.PriceKwd,
                Pages = b.Pages,
                Year = b.Year,
                SalesCount = b.SalesCount,
                TitleEn = b.TitleEn,
                AuthorEn = b.AuthorEn,
                DescriptionEn = b.DescriptionEn,
                TitleAr = b.TitleAr,
                AuthorAr = b.AuthorAr,
                DescriptionAr = b.DescriptionAr,
                ContentLanguages = BookLanguages.ArabicAndEnglish,
                // Spread across the past so "newest" (D1) has a meaningful order
                // instead of twenty books added in the same millisecond.
                AddedToStoreAt = now.AddDays(-(seed.Books.Count - b.Id)),
                CreatedAt = now,
                UpdatedAt = now,
                IsPublished = false
            };

            // Never publish something with an incomplete record, whatever the caller asked.
            //
            // ContentBlocker, not PublishBlocker: the seed carries no PDF, and development
            // and staging need a browsable catalogue to work against. The stricter gate —
            // a real, non-placeholder file — is enforced by the admin's publish action, so
            // no operator can put a fileless title on sale in any environment. Production
            // seeds unpublished regardless (decision D4).
            if (publishBooks)
            {
                var blocker = book.ContentBlocker();
                if (blocker is null) book.IsPublished = true;
                else log.LogWarning("Seed book {Id} left unpublished: {Blocker}", b.Id, blocker);
            }

            db.Books.Add(book);
            seeded++;
        }

        if (seeded > 0) await db.SaveChangesAsync(ct);

        log.LogInformation(
            "Catalogue seed: {Categories} categories, {Books} books added ({State}).",
            added, seeded, publishBooks ? "published" : "unpublished — rights not cleared");
    }

    private static SeedFile Load()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("catalogue.json", StringComparison.Ordinal));

        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Catalogue seed resource is missing.");

        return JsonSerializer.Deserialize<SeedFile>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Catalogue seed could not be read.");
    }
}
