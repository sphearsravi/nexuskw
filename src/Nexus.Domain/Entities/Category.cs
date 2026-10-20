namespace Nexus.Domain.Entities;

/// <summary>
/// A subject the catalogue is organised by. The slug is the public identifier
/// and is what the storefront already uses ("investing", "islamic"), so it is
/// the key rather than a surrogate id — CAT-05 requires addresses to stay
/// stable across a reseed.
/// </summary>
public class Category
{
    public required string Slug { get; set; }

    public required string NameEn { get; set; }
    public required string NameAr { get; set; }

    /// <summary>Accent colour the drawn covers and the subject grid use.</summary>
    public required string Hue { get; set; }

    /// <summary>Operator-defined display position (CAT-06).</summary>
    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public ICollection<Book> Books { get; set; } = [];
}
