using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexus.Domain.Entities;

namespace Nexus.Infrastructure.Persistence.Configurations;

public sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> e)
    {
        e.HasKey(c => c.Slug);
        e.Property(c => c.Slug).HasMaxLength(64);
        // NVARCHAR, not VARCHAR: a VARCHAR column silently mangles Arabic.
        e.Property(c => c.NameEn).HasMaxLength(128).IsRequired();
        e.Property(c => c.NameAr).HasMaxLength(128).IsRequired();
        e.Property(c => c.Hue).HasMaxLength(16).IsRequired();
        e.HasIndex(c => c.SortOrder);
    }
}

public sealed class BookConfiguration : IEntityTypeConfiguration<Book>
{
    public void Configure(EntityTypeBuilder<Book> e)
    {
        e.HasKey(x => x.Id);
        // Ids come from the seed and must survive a reseed (CAT-05).
        e.Property(x => x.Id).ValueGeneratedNever();

        e.Property(x => x.PriceKwd).HasColumnType("decimal(18,3)");

        e.Property(x => x.CategorySlug).HasMaxLength(64).IsRequired();
        e.Property(x => x.TitleEn).HasMaxLength(256);
        e.Property(x => x.AuthorEn).HasMaxLength(128);
        e.Property(x => x.DescriptionEn).HasMaxLength(2000);
        e.Property(x => x.TitleAr).HasMaxLength(256);
        e.Property(x => x.AuthorAr).HasMaxLength(128);
        e.Property(x => x.DescriptionAr).HasMaxLength(2000);
        e.Property(x => x.CoverMotif).HasMaxLength(64);
        e.Property(x => x.Isbn).HasMaxLength(32);
        e.Property(x => x.ContentLanguages).HasConversion<int>();

        e.Property(x => x.PdfStorageKey).HasMaxLength(256);
        e.Property(x => x.PdfSha256).HasMaxLength(64);
        e.Property(x => x.PdfOriginalFileName).HasMaxLength(256);
        e.Property(x => x.PdfUploadedByAdminUserId).HasMaxLength(450);
        e.Property(x => x.PdfIsPlaceholder).HasDefaultValue(false);

        e.HasOne(x => x.Category)
         .WithMany(c => c.Books)
         .HasForeignKey(x => x.CategorySlug)
         .OnDelete(DeleteBehavior.Restrict); // CAT-04: cannot orphan published books

        e.HasIndex(x => x.IsPublished);
        e.HasIndex(x => new { x.IsPublished, x.CategorySlug });
        e.HasIndex(x => new { x.IsPublished, x.AddedToStoreAt });
    }
}
