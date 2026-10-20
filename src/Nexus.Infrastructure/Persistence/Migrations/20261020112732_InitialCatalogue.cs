using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalogue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Categories",
                columns: table => new
                {
                    Slug = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Hue = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categories", x => x.Slug);
                });

            migrationBuilder.CreateTable(
                name: "Books",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    CategorySlug = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PriceKwd = table.Column<decimal>(type: "decimal(18,3)", nullable: false),
                    Pages = table.Column<int>(type: "int", nullable: false),
                    Year = table.Column<int>(type: "int", nullable: false),
                    AddedToStoreAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SalesCount = table.Column<int>(type: "int", nullable: false),
                    TitleEn = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    AuthorEn = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DescriptionEn = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    TitleAr = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    AuthorAr = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DescriptionAr = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ContentLanguages = table.Column<int>(type: "int", nullable: false),
                    CoverMotif = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Isbn = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    IsPublished = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Books", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Books_Categories_CategorySlug",
                        column: x => x.CategorySlug,
                        principalTable: "Categories",
                        principalColumn: "Slug",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Books_CategorySlug",
                table: "Books",
                column: "CategorySlug");

            migrationBuilder.CreateIndex(
                name: "IX_Books_IsPublished",
                table: "Books",
                column: "IsPublished");

            migrationBuilder.CreateIndex(
                name: "IX_Books_IsPublished_AddedToStoreAt",
                table: "Books",
                columns: new[] { "IsPublished", "AddedToStoreAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Books_IsPublished_CategorySlug",
                table: "Books",
                columns: new[] { "IsPublished", "CategorySlug" });

            migrationBuilder.CreateIndex(
                name: "IX_Categories_SortOrder",
                table: "Categories",
                column: "SortOrder");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Books");

            migrationBuilder.DropTable(
                name: "Categories");
        }
    }
}
