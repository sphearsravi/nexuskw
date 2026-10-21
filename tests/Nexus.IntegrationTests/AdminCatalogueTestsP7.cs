using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexus.Domain.Entities;
using Nexus.Domain;

namespace Nexus.IntegrationTests;

/// <summary>
/// The catalogue screens: the publish gate, the bulk import, and the two deletions that
/// are refused on purpose.
/// <para>
/// Every test cleans up the rows it creates. The collection shares one database with
/// tests that assert the seeded catalogue's exact size, so a stray published book here
/// would fail a catalogue test over there, which is a miserable thing to debug.
/// </para>
/// </summary>
[Collection(NexusCollection.Name)]
public sealed class AdminCatalogueTestsP7(NexusApp app) : IDisposable
{
    private readonly AdminHostP7 _host = new(app);

    public void Dispose() => _host.Dispose();

    // ---- the publish gate ---------------------------------------------------

    [Fact]
    public async Task A_title_with_no_file_cannot_be_published()
    {
        var id = await _host.NewDraftBookAsync("nofile");
        try
        {
            var response = await _host.AsStaff().PostJsonAsync($"/admin/api/books/{id}/publish", new { });

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("publish_blocked", body.GetProperty("error").GetString());

            // The gate's own sentence, not a restatement of it.
            Assert.Equal("PDF file is missing.", body.GetProperty("blocker").GetString());

            using var scope = _host.Scope();
            Assert.False(await _host.Db(scope).Books.AnyAsync(b => b.Id == id && b.IsPublished));
        }
        finally
        {
            await _host.RemoveBookAsync(id);
        }
    }

    /// <summary>
    /// B12: "unpublished in production" is not the same promise as "no placeholder files".
    /// A generated stub satisfies "a file exists" and must still be refused.
    /// </summary>
    [Fact]
    public async Task A_placeholder_file_does_not_satisfy_the_gate()
    {
        var id = await _host.NewDraftBookAsync("stub");
        try
        {
            using (var scope = _host.Scope())
            {
                await _host.Service<Nexus.Application.Delivery.IPdfLibrary>(scope).WritePlaceholderAsync(id);
            }

            var response = await _host.AsStaff().PostJsonAsync($"/admin/api/books/{id}/publish", new { });
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("PDF is a seed placeholder, not the real file.", body.GetProperty("blocker").GetString());
        }
        finally
        {
            await _host.RemoveBookAsync(id);
        }
    }

    [Fact]
    public async Task A_real_file_opens_the_gate_and_unpublishing_closes_it_again()
    {
        var id = await _host.NewDraftBookAsync("real");
        var client = _host.AsStaff();
        try
        {
            var upload = await client.UploadAsync(
                $"/admin/api/books/{id}/pdf", "file", "the-book.pdf", AdminHostP7.OnePagePdf());
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

            var published = await client.PostJsonAsync($"/admin/api/books/{id}/publish", new { });
            Assert.Equal(HttpStatusCode.OK, published.StatusCode);

            using (var scope = _host.Scope())
            {
                var book = await _host.Db(scope).Books.AsNoTracking().FirstAsync(b => b.Id == id);
                Assert.True(book.IsPublished);
                Assert.False(book.PdfIsPlaceholder);
                Assert.Equal("the-book.pdf", book.PdfOriginalFileName);
            }

            var off = await client.PostJsonAsync($"/admin/api/books/{id}/unpublish", new { });
            Assert.Equal(HttpStatusCode.OK, off.StatusCode);

            using (var scope = _host.Scope())
                Assert.False((await _host.Db(scope).Books.AsNoTracking().FirstAsync(b => b.Id == id)).IsPublished);
        }
        finally
        {
            await _host.RemoveBookAsync(id);
        }
    }

    [Fact]
    public async Task Something_that_is_not_a_pdf_is_refused_by_its_bytes_not_its_name()
    {
        var id = await _host.NewDraftBookAsync("notpdf");
        try
        {
            var response = await _host.AsStaff().UploadAsync(
                $"/admin/api/books/{id}/pdf", "file", "looks-fine.pdf",
                Encoding.ASCII.GetBytes("GIF89a this is not a pdf at all"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            using var scope = _host.Scope();
            Assert.Null((await _host.Db(scope).Books.AsNoTracking().FirstAsync(b => b.Id == id)).PdfStorageKey);
        }
        finally
        {
            await _host.RemoveBookAsync(id);
        }
    }

    // ---- validation ---------------------------------------------------------

    [Fact]
    public async Task A_book_without_its_arabic_edition_is_refused_field_by_field()
    {
        var response = await _host.AsStaff().PostJsonAsync("/admin/api/books", new
        {
            categorySlug = "investing",
            priceKwd = 5.500m,
            pages = 100,
            year = 2025,
            titleEn = "English only",
            authorEn = "Someone",
            descriptionEn = "Described.",
            titleAr = "",
            authorAr = "",
            descriptionAr = ""
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var fields = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("fields")
            .EnumerateArray()
            .Select(f => f.GetProperty("field").GetString())
            .ToList();

        // All three at once: an import that reports one error per attempt turns a
        // fifty-row file into a fifty-round correspondence.
        Assert.Contains("titleAr", fields);
        Assert.Contains("authorAr", fields);
        Assert.Contains("descriptionAr", fields);
    }

    [Fact]
    public async Task A_free_book_is_refused()
    {
        var response = await _host.AsStaff().PostJsonAsync("/admin/api/books", new
        {
            categorySlug = "investing",
            priceKwd = 0m,
            pages = 100,
            year = 2025,
            titleEn = "Free", authorEn = "A", descriptionEn = "D",
            titleAr = "مجاني", authorAr = "أ", descriptionAr = "و"
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // ---- deletion -----------------------------------------------------------

    [Fact]
    public async Task A_title_that_has_been_sold_cannot_be_deleted()
    {
        var id = await _host.NewDraftBookAsync("sold");
        try
        {
            await SellAsync(id);

            var response = await _host.AsAdmin().DeleteAsync($"/admin/api/books/{id}");

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("book_has_orders", await response.Content.ReadAsStringAsync());

            using var scope = _host.Scope();
            Assert.True(await _host.Db(scope).Books.AnyAsync(b => b.Id == id));
        }
        finally
        {
            await _host.RemoveBookAsync(id);
        }
    }

    [Fact]
    public async Task An_unsold_draft_can_be_deleted_and_the_deletion_is_audited()
    {
        var id = await _host.NewDraftBookAsync("unsold");
        var response = await _host.AsAdmin().DeleteAsync($"/admin/api/books/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);

        using var scope = _host.Scope();
        var db = _host.Db(scope);
        Assert.False(await db.Books.AnyAsync(b => b.Id == id));
        Assert.True(await db.AuditLogs.AnyAsync(a =>
            a.Entity == "Book" && a.EntityId == key && a.Action == "book.delete"));
    }

    // ---- categories ---------------------------------------------------------

    [Fact]
    public async Task A_subject_that_still_has_books_cannot_be_deleted()
    {
        var response = await _host.AsAdmin().DeleteAsync("/admin/api/categories/investing");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("category_has_books", await response.Content.ReadAsStringAsync());

        using var scope = _host.Scope();
        Assert.True(await _host.Db(scope).Categories.AnyAsync(c => c.Slug == "investing"));
    }

    [Fact]
    public async Task A_subject_is_created_renamed_and_removed()
    {
        const string slug = "p7-temporary";
        var client = _host.AsAdmin();

        try
        {
            var created = await client.PostJsonAsync($"/admin/api/categories/{slug}", new
            {
                nameEn = "Temporary",
                nameAr = "مؤقت",
                hue = "#123456",
                isActive = true
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            var renamed = await client.PutJsonAsync($"/admin/api/categories/{slug}", new
            {
                nameEn = "Renamed",
                nameAr = "معاد تسميته",
                hue = "#123456",
                isActive = true
            });
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

            using (var scope = _host.Scope())
            {
                var row = await _host.Db(scope).Categories.AsNoTracking().FirstAsync(c => c.Slug == slug);
                Assert.Equal("Renamed", row.NameEn);
            }

            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/admin/api/categories/{slug}")).StatusCode);
        }
        finally
        {
            await _host.RemoveCategoryAsync(slug);
        }
    }

    [Fact]
    public async Task A_subject_without_its_arabic_name_is_refused()
    {
        var response = await _host.AsAdmin().PostJsonAsync("/admin/api/categories/p7-no-arabic", new
        {
            nameEn = "English only", nameAr = "", hue = "#123456", isActive = true
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var scope = _host.Scope();
        Assert.False(await _host.Db(scope).Categories.AnyAsync(c => c.Slug == "p7-no-arabic"));
    }

    [Fact]
    public async Task Reordering_must_name_every_subject()
    {
        var response = await _host.AsStaff().PostJsonAsync("/admin/api/categories/reorder", new
        {
            slugs = new[] { "investing" }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("reorder_incomplete", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Reordering_moves_a_subject_and_puts_it_back()
    {
        var client = _host.AsStaff();
        var before = await Slugs(client);

        var moved = before.Skip(1).Take(1).Concat(before.Where((_, i) => i != 1)).ToArray();
        Assert.Equal(HttpStatusCode.OK, (await client.PostJsonAsync("/admin/api/categories/reorder",
            new { slugs = moved })).StatusCode);
        Assert.Equal(moved, await Slugs(client));

        Assert.Equal(HttpStatusCode.OK, (await client.PostJsonAsync("/admin/api/categories/reorder",
            new { slugs = before })).StatusCode);
        Assert.Equal(before, await Slugs(client));
    }

    // ---- bulk import --------------------------------------------------------

    private const string Header =
        "category_slug,price_kwd,pages,year,isbn,title_en,author_en,description_en," +
        "title_ar,author_ar,description_ar,content_languages,cover_motif\r\n";

    private static string Row(string title, string price = "3.750", string slug = "investing") =>
        $"{slug},{price},100,2025,,{title},Test Author,\"A description, with a comma.\"," +
        "عنوان,مؤلف,وصف,ArabicAndEnglish,\r\n";

    [Fact]
    public async Task One_bad_row_means_nothing_is_written()
    {
        var csv = Header + Row("P7 Import Good One") + Row("P7 Import Bad One", price: "nonsense");

        var before = await BookCountAsync();
        var response = await _host.AsStaff().UploadAsync(
            "/admin/api/books/import", "file", "books.csv", Encoding.UTF8.GetBytes(csv));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.False(body.GetProperty("accepted").GetBoolean());
        Assert.Equal(0, body.GetProperty("created").GetInt32());

        var errors = body.GetProperty("errors").EnumerateArray().ToList();
        Assert.Contains(errors, e => e.GetProperty("field").GetString() == "priceKwd"
                                     && e.GetProperty("line").GetInt32() == 3);

        Assert.Equal(before, await BookCountAsync());
    }

    [Fact]
    public async Task A_dry_run_validates_and_writes_nothing()
    {
        var csv = Header + Row("P7 Dry Run One") + Row("P7 Dry Run Two");

        var before = await BookCountAsync();
        var response = await _host.AsStaff().UploadAsync(
            "/admin/api/books/import?dryRun=true", "file", "books.csv", Encoding.UTF8.GetBytes(csv));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.GetProperty("dryRun").GetBoolean());
        Assert.Equal(0, body.GetProperty("created").GetInt32());
        Assert.Equal(2, body.GetProperty("rowsRead").GetInt32());
        Assert.Equal(before, await BookCountAsync());
    }

    [Fact]
    public async Task A_good_file_lands_as_drafts_that_still_need_their_files()
    {
        var csv = Header + Row("P7 Landed One") + Row("P7 Landed Two", price: "9.125");
        int[] created = [];

        try
        {
            var response = await _host.AsStaff().UploadAsync(
                "/admin/api/books/import", "file", "books.csv", Encoding.UTF8.GetBytes(csv));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            created = body.GetProperty("createdBookIds").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            Assert.Equal(2, created.Length);

            using var scope = _host.Scope();
            var db = _host.Db(scope);
            var books = await db.Books.AsNoTracking().Where(b => created.Contains(b.Id)).ToListAsync();

            Assert.All(books, b => Assert.False(b.IsPublished));
            Assert.All(books, b => Assert.Null(b.PdfStorageKey));
            Assert.Contains(books, b => b.PriceKwd == 9.125m);

            // The comma inside the quoted description survived the parse.
            Assert.All(books, b => Assert.Equal("A description, with a comma.", b.DescriptionEn));

            // Each imported title is answerable on its own, not only as a batch.
            var keys = created.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Assert.Equal(2, await db.AuditLogs.CountAsync(a =>
                a.Action == "book.import" && keys.Contains(a.EntityId)));
        }
        finally
        {
            if (created.Length > 0) await _host.RemoveBookAsync(created);
        }
    }

    [Fact]
    public async Task A_file_that_repeats_a_title_is_refused_before_it_doubles_the_shelf()
    {
        var csv = Header + Row("P7 Repeated") + Row("P7 Repeated");

        var before = await BookCountAsync();
        var response = await _host.AsStaff().UploadAsync(
            "/admin/api/books/import", "file", "books.csv", Encoding.UTF8.GetBytes(csv));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("duplicate_in_file", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await BookCountAsync());
    }

    [Fact]
    public async Task A_file_missing_a_required_column_is_refused_at_the_header()
    {
        var csv = "category_slug,price_kwd\r\ninvesting,3.000\r\n";

        var response = await _host.AsStaff().UploadAsync(
            "/admin/api/books/import", "file", "books.csv", Encoding.UTF8.GetBytes(csv));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("missing_column", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_template_is_offered_with_a_bom_so_excel_reads_the_arabic()
    {
        var response = await _host.AsStaff().GetAsync("/admin/api/books/import/template");
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        Assert.Contains("title_ar", Encoding.UTF8.GetString(bytes));
    }

    // ---- helpers -------------------------------------------------------------

    private static async Task<string[]> Slugs(PanelClient client)
    {
        var rows = await client.GetJsonAsync("/admin/api/categories");
        return rows.EnumerateArray().Select(c => c.GetProperty("slug").GetString()!).ToArray();
    }

    private async Task<int> BookCountAsync()
    {
        using var scope = _host.Scope();
        return await _host.Db(scope).Books.CountAsync();
    }

    /// <summary>A paid order holding this title, so the delete guard has something to find.</summary>
    private async Task SellAsync(int bookId)
    {
        using var scope = _host.Scope();
        var db = _host.Db(scope);
        var book = await db.Books.AsNoTracking().FirstAsync(b => b.Id == bookId);
        var now = DateTimeOffset.UtcNow;

        var customer = new Customer
        {
            Email = $"sold-{bookId}@example.com",
            NormalisedEmail = Customer.Normalise($"sold-{bookId}@example.com"),
            Name = "Buyer",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        db.Orders.Add(new Order
        {
            OrderNumber = $"KW-P7S-{bookId:D6}",
            CustomerId = customer.Id,
            BuyerNameSnapshot = "Buyer",
            BuyerEmailSnapshot = customer.Email,
            Status = OrderStatus.Paid,
            Language = "en",
            SubtotalKwd = book.PriceKwd,
            TotalKwd = book.PriceKwd,
            TotalCharged = book.PriceKwd,
            PaymentExpiresAt = now.AddHours(24),
            PaidAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            Items =
            [
                new OrderItem
                {
                    BookId = bookId,
                    UnitPriceKwd = book.PriceKwd,
                    TitleEnSnapshot = book.TitleEn,
                    TitleArSnapshot = book.TitleAr,
                    AuthorEnSnapshot = book.AuthorEn,
                    AuthorArSnapshot = book.AuthorAr
                }
            ]
        });

        await db.SaveChangesAsync();
    }
}
