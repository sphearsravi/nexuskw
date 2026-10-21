using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nexus.Domain.Entities;
using Nexus.Infrastructure;
using Nexus.Infrastructure.Persistence;

namespace Nexus.IntegrationTests;

/// <summary>
/// The shared <see cref="NexusApp"/> with Phase 7's services added and a throwaway
/// <c>App_Data</c> root, following <see cref="DeliveryHost"/>.
/// <para>
/// <c>Program.cs</c> does not call <c>AddNexusAdmin</c> yet — the integrator adds that
/// line — so the tests add it the way the application will: one call, nothing else
/// changed.
/// </para>
/// </summary>
public sealed class AdminHostP7 : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public AdminHostP7(NexusApp app)
    {
        LibraryRoot = Path.Combine(Path.GetTempPath(), "nexus-admin-" + Guid.NewGuid().ToString("N")[..8]);

        _factory = app.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nexus:Library:Root"] = LibraryRoot
                }));

            builder.ConfigureServices(services => services.AddNexusAdmin());
        });
    }

    public string LibraryRoot { get; }

    public IServiceScope Scope() => _factory.Services.CreateScope();

    public T Service<T>(IServiceScope scope) where T : notnull =>
        scope.ServiceProvider.GetRequiredService<T>();

    public NexusDbContext Db(IServiceScope scope) => Service<NexusDbContext>(scope);

    /// <summary>No cookie at all.</summary>
    public PanelClient Anonymous() => new(NewHttp());

    public PanelClient AsAdmin(string email = "boss@nexus.test") =>
        WithCookie(NexusAuth.AdminScheme, "admin-user", email, NexusRoles.Admin);

    public PanelClient AsStaff(string email = "desk@nexus.test") =>
        WithCookie(NexusAuth.AdminScheme, "staff-user", email, NexusRoles.Staff);

    /// <summary>
    /// A genuine, correctly signed <b>customer</b> cookie. The point of the test it
    /// serves is that a real session on the cheap credential opens nothing here — which a
    /// request carrying no cookie at all would not prove.
    /// </summary>
    public PanelClient AsCustomer(string email = "reader@example.com") =>
        WithCookie(NexusAuth.CustomerScheme, "customer-user", email, NexusRoles.Customer);

    /// <summary>An administrator's cookie minted onto the <i>customer</i> scheme.</summary>
    public PanelClient AsAdminOnTheWrongCookie(string email = "boss@nexus.test") =>
        WithCookie(NexusAuth.CustomerScheme, "admin-user", email, NexusRoles.Admin);

    private PanelClient WithCookie(string scheme, string userId, string email, params string[] roles)
    {
        var options = _factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(scheme);

        // ClaimTypes.Role is what IdentityOptions.ClaimsIdentity.RoleClaimType defaults to,
        // so RequireRole in the policies reads these.
        var identity = new ClaimsIdentity("Identity.Application", ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, userId));
        identity.AddClaim(new Claim(ClaimTypes.Name, email));
        identity.AddClaim(new Claim(ClaimTypes.Email, email));
        foreach (var role in roles) identity.AddClaim(new Claim(ClaimTypes.Role, role));

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), scheme);

        var client = new PanelClient(NewHttp());
        client.SetCookie(options.Cookie.Name!, options.TicketDataFormat.Protect(ticket));
        return client;
    }

    private HttpClient NewHttp() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    // ---- fixtures the catalogue tests build on ------------------------------

    /// <summary>
    /// A draft title with no file, which is the state the publish gate exists to catch.
    /// The caller is responsible for <see cref="RemoveBookAsync"/>: the collection shares
    /// one database with tests that assert the seeded catalogue's exact size.
    /// </summary>
    public async Task<int> NewDraftBookAsync(string tag, string categorySlug = "investing")
    {
        using var scope = Scope();
        var db = Db(scope);
        var now = DateTimeOffset.UtcNow;

        var book = new Book
        {
            Id = (await db.Books.MaxAsync(b => (int?)b.Id) ?? 0) + 1,
            CategorySlug = categorySlug,
            PriceKwd = 4.250m,
            Pages = 120,
            Year = 2025,
            TitleEn = $"Draft {tag}",
            AuthorEn = "Test Author",
            DescriptionEn = "A draft used by the admin tests.",
            TitleAr = $"مسودة {tag}",
            AuthorAr = "مؤلف اختبار",
            DescriptionAr = "مسودة للاختبار.",
            IsPublished = false,
            AddedToStoreAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Books.Add(book);
        await db.SaveChangesAsync();
        return book.Id;
    }

    public async Task RemoveBookAsync(params int[] ids)
    {
        using var scope = Scope();
        var db = Db(scope);

        var grants = await db.DownloadGrants.Where(g => ids.Contains(g.BookId)).Select(g => g.Id).ToListAsync();
        db.DownloadLogs.RemoveRange(await db.DownloadLogs.Where(l => grants.Contains(l.GrantId)).ToListAsync());
        db.DownloadGrants.RemoveRange(await db.DownloadGrants.Where(g => ids.Contains(g.BookId)).ToListAsync());
        db.OrderItems.RemoveRange(await db.OrderItems.Where(i => ids.Contains(i.BookId)).ToListAsync());
        db.Books.RemoveRange(await db.Books.Where(b => ids.Contains(b.Id)).ToListAsync());
        await db.SaveChangesAsync();
    }

    public async Task RemoveCategoryAsync(params string[] slugs)
    {
        using var scope = Scope();
        var db = Db(scope);
        db.Categories.RemoveRange(await db.Categories.Where(c => slugs.Contains(c.Slug)).ToListAsync());
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A genuine one-page PDF. It has to be a real document: the library validates magic
    /// bytes, the trailer and the structure, and a hand-written stub fails the last of
    /// those — which is the point of the check.
    /// </summary>
    public static byte[] OnePagePdf() => OnePage.Value;

    private static readonly Lazy<byte[]> OnePage = new(() =>
    {
        using var doc = new PdfSharp.Pdf.PdfDocument();
        doc.AddPage();
        using var buffer = new MemoryStream();
        doc.Save(buffer, closeStream: false);
        return buffer.ToArray();
    });

    public void Dispose()
    {
        _factory.Dispose();

        try
        {
            if (Directory.Exists(LibraryRoot)) Directory.Delete(LibraryRoot, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is not worth failing a test run over.
        }
    }
}

/// <summary>
/// An HTTP client that behaves like the control panel: it keeps its cookies, and it
/// carries the antiforgery token on everything that is not a GET.
/// <para>
/// Cookies are managed by hand rather than with <c>HandleCookies</c>, because the auth
/// cookie is minted rather than obtained from a sign-in and the two mechanisms fight over
/// the <c>Cookie</c> header.
/// </para>
/// </summary>
public sealed class PanelClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);
    private string? _csrf;

    /// <summary>Set by the test that proves a write without the token is refused.</summary>
    public bool SkipAntiforgery { get; set; }

    public void SetCookie(string name, string value) => _cookies[name] = value;

    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, HttpContent? content = null, string accept = "application/json")
    {
        if (method != HttpMethod.Get && _csrf is null && !SkipAntiforgery) await PrimeAsync();

        var request = new HttpRequestMessage(method, url);
        if (content is not null) request.Content = content;

        request.Headers.Accept.ParseAdd(accept);
        if (_cookies.Count > 0)
            request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));
        if (method != HttpMethod.Get && _csrf is not null)
            request.Headers.Add("RequestVerificationToken", _csrf);

        var response = await http.SendAsync(request);
        Capture(response);
        return response;
    }

    public Task<HttpResponseMessage> GetAsync(string url) => SendAsync(HttpMethod.Get, url);

    public Task<HttpResponseMessage> PostJsonAsync(string url, object? body) =>
        SendAsync(HttpMethod.Post, url, JsonBody(body));

    public Task<HttpResponseMessage> PutJsonAsync(string url, object? body) =>
        SendAsync(HttpMethod.Put, url, JsonBody(body));

    public Task<HttpResponseMessage> DeleteAsync(string url) => SendAsync(HttpMethod.Delete, url);

    public async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    public async Task<JsonElement> GetJsonAsync(string url)
    {
        var response = await GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await JsonAsync(response);
    }

    public async Task<HttpResponseMessage> UploadAsync(string url, string field, string fileName, byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, field, fileName);
        return await SendAsync(HttpMethod.Post, url, form);
    }

    private static StringContent JsonBody(object? body) =>
        new(JsonSerializer.Serialize(body ?? new { }, Json), Encoding.UTF8, "application/json");

    /// <summary>The session endpoint is anonymous, which is what lets the login form post.</summary>
    private async Task PrimeAsync()
    {
        var response = await SendAsync(HttpMethod.Get, "/admin/api/session");
        var body = await JsonAsync(response);
        if (body.TryGetProperty("csrf", out var token)) _csrf = token.GetString();
    }

    private void Capture(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return;

        foreach (var raw in values)
        {
            var pair = raw.Split(';')[0];
            var at = pair.IndexOf('=');
            if (at <= 0) continue;

            var name = pair[..at].Trim();
            var value = pair[(at + 1)..].Trim();

            if (value.Length == 0) _cookies.Remove(name);
            else _cookies[name] = value;
        }
    }
}
