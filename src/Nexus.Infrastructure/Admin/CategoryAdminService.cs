using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Nexus.Application.Common;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Admin;

/// <summary>
/// Subjects: create, rename, recolour, reorder, activate.
/// <para>
/// The slug is the public identifier and never changes after creation (CAT-05): the
/// storefront links to <c>shop.html?cat=investing</c> and a rename that moved the slug
/// would break every link anybody ever shared.
/// </para>
/// </summary>
public sealed partial class CategoryAdminService(NexusDbContext db, IAuditLog audit)
{
    public const int MaxName = 128;

    public async Task<IReadOnlyList<AdminCategoryRow>> ListAsync(CancellationToken ct = default) =>
        await db.Categories.AsNoTracking()
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Slug)
            .Select(c => new AdminCategoryRow(
                c.Slug, c.NameEn, c.NameAr, c.Hue, c.SortOrder, c.IsActive,
                c.Books.Count,
                c.Books.Count(b => b.IsPublished)))
            .ToListAsync(ct);

    public async Task<(AdminCategoryRow? Row, IReadOnlyList<FieldError> Errors)> CreateAsync(
        string slug, CategoryWrite write, CancellationToken ct = default)
    {
        var cleanSlug = (slug ?? "").Trim().ToLowerInvariant();
        var errors = Check(write);

        if (cleanSlug.Length == 0) errors.Add(new FieldError(0, "slug", "required"));
        else if (!SlugShape().IsMatch(cleanSlug)) errors.Add(new FieldError(0, "slug", "bad_shape"));
        else if (await db.Categories.AnyAsync(c => c.Slug == cleanSlug, ct))
            errors.Add(new FieldError(0, "slug", "already_exists", cleanSlug));

        if (errors.Count > 0) return (null, errors);

        var next = (await db.Categories.MaxAsync(c => (int?)c.SortOrder, ct) ?? -1) + 1;
        var category = new Category
        {
            Slug = cleanSlug,
            NameEn = write.NameEn.Trim(),
            NameAr = write.NameAr.Trim(),
            Hue = write.Hue.Trim(),
            SortOrder = next,
            IsActive = write.IsActive
        };

        db.Categories.Add(category);
        await audit.RecordAsync("Category", cleanSlug, "category.create", null, Snapshot(category), ct);
        await db.SaveChangesAsync(ct);

        return (await OneAsync(cleanSlug, ct), []);
    }

    public async Task<(AdminCategoryRow? Row, IReadOnlyList<FieldError> Errors, AdminProblem? Problem)> UpdateAsync(
        string slug, CategoryWrite write, CancellationToken ct = default)
    {
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Slug == slug, ct);
        if (category is null) return (null, [], new AdminProblem("category_not_found"));

        var errors = Check(write);
        if (errors.Count > 0) return (null, errors, null);

        var before = Snapshot(category);

        category.NameEn = write.NameEn.Trim();
        category.NameAr = write.NameAr.Trim();
        category.Hue = write.Hue.Trim();
        category.IsActive = write.IsActive;

        /* Deactivating a subject takes its published titles off sale with it. Leaving them
         * live would contradict Book.ContentBlocker, which already calls an inactive
         * category a reason a book may not be published. */
        var pulled = 0;
        if (!category.IsActive)
        {
            // Tracked, not ExecuteUpdate: that commits on its own, so a later failure here
            // would leave the titles off sale and the subject still active.
            var live = await db.Books.Where(b => b.CategorySlug == slug && b.IsPublished).ToListAsync(ct);
            foreach (var b in live) b.IsPublished = false;
            pulled = live.Count;
        }

        await audit.RecordAsync("Category", slug,
            pulled > 0 ? "category.deactivate" : "category.update",
            before, new { Category = Snapshot(category), BooksUnpublished = pulled }, ct);

        await db.SaveChangesAsync(ct);
        return (await OneAsync(slug, ct), [], null);
    }

    /// <summary>
    /// Reorders by the sequence given. Every existing slug must appear exactly once — a
    /// partial list would leave the rest holding positions that now collide, and the
    /// operator would see an order nobody asked for.
    /// </summary>
    public async Task<(IReadOnlyList<AdminCategoryRow>? Rows, AdminProblem? Problem)> ReorderAsync(
        IReadOnlyList<string> slugs, CancellationToken ct = default)
    {
        var all = await db.Categories.ToListAsync(ct);
        var wanted = slugs.Select(s => s.Trim().ToLowerInvariant()).ToList();

        if (wanted.Count != all.Count || wanted.Distinct(StringComparer.Ordinal).Count() != wanted.Count)
            return (null, new AdminProblem("reorder_incomplete"));

        if (wanted.Any(s => all.All(c => c.Slug != s)))
            return (null, new AdminProblem("reorder_unknown_slug"));

        var before = all.OrderBy(c => c.SortOrder).Select(c => c.Slug).ToList();

        for (var i = 0; i < wanted.Count; i++)
            all.First(c => c.Slug == wanted[i]).SortOrder = i;

        await audit.RecordAsync("Category", "*", "category.reorder", before, wanted, ct);
        await db.SaveChangesAsync(ct);

        return (await ListAsync(ct), null);
    }

    /// <summary>
    /// Refused while any book still points here, published or not. The foreign key is
    /// Restrict, so the alternative is a database error the operator cannot read.
    /// </summary>
    public async Task<AdminProblem?> DeleteAsync(string slug, CancellationToken ct = default)
    {
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Slug == slug, ct);
        if (category is null) return new AdminProblem("category_not_found");

        var books = await db.Books.CountAsync(b => b.CategorySlug == slug, ct);
        if (books > 0)
            return new AdminProblem("category_has_books", books.ToString(System.Globalization.CultureInfo.InvariantCulture));

        db.Categories.Remove(category);
        await audit.RecordAsync("Category", slug, "category.delete", Snapshot(category), null, ct);
        await db.SaveChangesAsync(ct);

        return null;
    }

    private async Task<AdminCategoryRow?> OneAsync(string slug, CancellationToken ct) =>
        (await ListAsync(ct)).FirstOrDefault(c => c.Slug == slug);

    private static List<FieldError> Check(CategoryWrite write)
    {
        var errors = new List<FieldError>();

        if (string.IsNullOrWhiteSpace(write.NameEn)) errors.Add(new FieldError(0, "nameEn", "required"));
        else if (write.NameEn.Length > MaxName) errors.Add(new FieldError(0, "nameEn", "too_long"));

        // Arabic is not optional: the subject menu renders in both languages on every page.
        if (string.IsNullOrWhiteSpace(write.NameAr)) errors.Add(new FieldError(0, "nameAr", "required"));
        else if (write.NameAr.Length > MaxName) errors.Add(new FieldError(0, "nameAr", "too_long"));

        // The hue is interpolated straight into a style attribute by the storefront, so it
        // is constrained to a hex colour here rather than escaped at every use.
        if (string.IsNullOrWhiteSpace(write.Hue)) errors.Add(new FieldError(0, "hue", "required"));
        else if (!HexColour().IsMatch(write.Hue.Trim())) errors.Add(new FieldError(0, "hue", "bad_shape"));

        return errors;
    }

    private static object Snapshot(Category c) => new { c.Slug, c.NameEn, c.NameAr, c.Hue, c.SortOrder, c.IsActive };

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,62}$")]
    private static partial Regex SlugShape();

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColour();
}
