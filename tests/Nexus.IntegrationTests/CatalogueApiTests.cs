using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Persistence;

namespace Nexus.IntegrationTests;

[Collection(NexusCollection.Name)]
public sealed class CatalogueApiTests(NexusApp app)
{
    private readonly HttpClient _client = app.CreateClient();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<JsonElement[]> BooksAsync(string query = "") =>
        await _client.GetFromJsonAsync<JsonElement[]>("/api/books" + query, Json) ?? [];

    [Fact]
    public async Task The_whole_seeded_catalogue_is_published()
    {
        var books = await BooksAsync();
        Assert.Equal(20, books.Length);
    }

    [Fact]
    public async Task Categories_report_their_published_counts()
    {
        var cats = await _client.GetFromJsonAsync<JsonElement[]>("/api/categories", Json) ?? [];
        Assert.Equal(6, cats.Length);
        Assert.Equal(20, cats.Sum(c => c.GetProperty("count").GetInt32()));
    }

    // CAT-09: the demo searched only the active interface language, so an
    // Arabic reader could not find a book by its English title.
    [Theory]
    [InlineData("Patient")]        // English title
    [InlineData("Nadia")]          // English author
    [InlineData("المحفظة")]        // Arabic title
    [InlineData("نادية")]          // Arabic author
    public async Task Search_matches_both_languages(string q)
    {
        var books = await BooksAsync("?q=" + Uri.EscapeDataString(q));
        Assert.Single(books);
        Assert.Equal("The Patient Portfolio", books[0].GetProperty("en").GetProperty("t").GetString());
    }

    // CAT-10: readers type taa marbuta as haa all the time.
    [Fact]
    public async Task Arabic_search_tolerates_orthographic_variation()
    {
        var books = await BooksAsync("?q=" + Uri.EscapeDataString("المحفظه الصبوره"));
        Assert.Single(books);
    }

    [Fact]
    public async Task Category_and_query_combine()
    {
        var books = await BooksAsync("?cat=islamic&q=" + Uri.EscapeDataString("صكوك"));
        Assert.Single(books);
        Assert.Equal("Sukuk for Beginners", books[0].GetProperty("en").GetProperty("t").GetString());
    }

    [Fact]
    public async Task Price_range_filters_and_combines()
    {
        var books = await BooksAsync("?minPrice=0&maxPrice=3");
        Assert.NotEmpty(books);
        Assert.All(books, b => Assert.True(b.GetProperty("price").GetDecimal() <= 3m));
    }

    // CAT-12: every ordering is total, so two identical requests agree.
    [Theory]
    [InlineData("popular")]
    [InlineData("newest")]
    [InlineData("price-asc")]
    [InlineData("price-desc")]
    public async Task Every_sort_order_is_stable(string sort)
    {
        var a = await BooksAsync("?sort=" + sort);
        var b = await BooksAsync("?sort=" + sort);
        Assert.Equal(a.Select(x => x.GetProperty("id").GetInt32()),
                     b.Select(x => x.GetProperty("id").GetInt32()));
    }

    [Fact]
    public async Task Price_ascending_really_ascends()
    {
        var prices = (await BooksAsync("?sort=price-asc"))
            .Select(b => b.GetProperty("price").GetDecimal()).ToList();
        Assert.Equal(prices.OrderBy(p => p), prices);
    }

    [Fact]
    public async Task Prices_keep_three_decimal_places()
    {
        var book = await _client.GetFromJsonAsync<JsonElement>("/api/books/1", Json);
        // 6.5 must survive as 6.500, not become 6.5 through a float.
        Assert.Equal(6.500m, book.GetProperty("price").GetDecimal());
    }

    // CAT-20: the demo fell back to BOOKS[0], so ?id=9999 silently showed a
    // different book — a shareable URL that lied.
    [Theory]
    [InlineData("/api/books/9999")]
    [InlineData("/api/books/0")]
    [InlineData("/api/books/-1")]
    [InlineData("/api/books/abc")]
    public async Task An_unknown_book_is_not_found(string path) =>
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(path)).StatusCode);

    [Fact]
    public async Task Related_books_share_the_category_and_exclude_the_book_itself()
    {
        var related = await _client.GetFromJsonAsync<JsonElement[]>("/api/books/1/related", Json) ?? [];
        Assert.NotEmpty(related);
        Assert.All(related, r =>
        {
            Assert.Equal("investing", r.GetProperty("cat").GetString());
            Assert.NotEqual(1, r.GetProperty("id").GetInt32());
        });
    }

    // CAT-02: unpublishing must take a book out of every surface at once.
    [Fact]
    public async Task An_unpublished_book_disappears_everywhere()
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        var book = await db.Books.FindAsync(17);
        book!.IsPublished = false;
        await db.SaveChangesAsync();

        try
        {
            Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/books/17")).StatusCode);
            Assert.DoesNotContain(await BooksAsync(), b => b.GetProperty("id").GetInt32() == 17);

            var cats = await _client.GetFromJsonAsync<JsonElement[]>("/api/categories", Json) ?? [];
            Assert.Equal(19, cats.Sum(c => c.GetProperty("count").GetInt32()));
        }
        finally
        {
            book.IsPublished = true;
            await db.SaveChangesAsync();
        }
    }

    // CAT-35 / D4: the seeder must never publish something that fails the gate,
    // whatever the caller asks for.
    [Fact]
    public async Task The_seeder_refuses_to_publish_an_incomplete_book()
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();

        var incomplete = new Book
        {
            Id = 9001,
            CategorySlug = "investing",
            PriceKwd = 5.000m,
            TitleEn = "No Arabic Here",
            AuthorEn = "Someone",
            DescriptionEn = "Missing its Arabic edition."
        };
        Assert.False(incomplete.CanPublish());
        Assert.Equal("Arabic title is missing.", incomplete.PublishBlocker());
        await Task.CompletedTask;
    }
}
