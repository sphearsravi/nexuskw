using Microsoft.EntityFrameworkCore;
using Nexus.Domain.Entities;

namespace Nexus.Infrastructure.Persistence;

public class NexusDbContext(DbContextOptions<NexusDbContext> options) : DbContext(options)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Book> Books => Set<Book>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Category>(e =>
        {
            e.HasKey(c => c.Slug);
            e.Property(c => c.Slug).HasMaxLength(64);
            // NVARCHAR, not VARCHAR: a VARCHAR column silently mangles Arabic.
            e.Property(c => c.NameEn).HasMaxLength(128).IsRequired();
            e.Property(c => c.NameAr).HasMaxLength(128).IsRequired();
            e.Property(c => c.Hue).HasMaxLength(16).IsRequired();
            e.HasIndex(c => c.SortOrder);
        });

        b.Entity<Book>(e =>
        {
            e.HasKey(x => x.Id);
            // Ids are assigned by the seed and must survive a reseed (CAT-05),
            // so they are not database-generated.
            e.Property(x => x.Id).ValueGeneratedNever();

            // KWD is a three-decimal currency. This is the storage half of the
            // rule; Nexus.Domain.Currency is the formatting half.
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

            e.HasOne(x => x.Category)
             .WithMany(c => c.Books)
             .HasForeignKey(x => x.CategorySlug)
             .OnDelete(DeleteBehavior.Restrict); // CAT-04: cannot orphan published books

            // The shop's three hot paths.
            e.HasIndex(x => x.IsPublished);
            e.HasIndex(x => new { x.IsPublished, x.CategorySlug });
            e.HasIndex(x => new { x.IsPublished, x.AddedToStoreAt });
        });
    }
}
