using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Mvc;
using Nexus.Application.Catalogue;

namespace Nexus.Web.Controllers;

/// <summary>
/// The things a shop needs in order to be found, and which every mature storefront
/// ships and this one did not: a sitemap, a robots policy, and structured data.
/// <para>
/// Both are generated rather than static files, because a static sitemap of a
/// catalogue that changes is a sitemap that lies.
/// </para>
/// </summary>
[ApiController]
public sealed class DiscoveryController(
    ICatalogueService catalogue,
    IWebHostEnvironment env,
    IConfiguration config) : ControllerBase
{
    private string BaseUrl =>
        (config["Site:PublicBaseUrl"] ?? $"{Request.Scheme}://{Request.Host}").TrimEnd('/');

    /// <summary>
    /// Crawlers get the shop and nothing else.
    /// <para>
    /// A non-production deployment disallows everything: a staging copy of a store
    /// competing with the real one in search results is a problem that is tedious to
    /// undo once indexed.
    /// </para>
    /// </summary>
    [HttpGet("/robots.txt")]
    [Produces("text/plain")]
    public ContentResult Robots()
    {
        if (!env.IsProduction())
            return Content("User-agent: *\nDisallow: /\n", "text/plain", Encoding.UTF8);

        var body = new StringBuilder()
            .AppendLine("User-agent: *")
            .AppendLine("Allow: /")
            // Nothing below is useful to a crawler, and some of it is a credential.
            .AppendLine("Disallow: /admin/")
            .AppendLine("Disallow: /api/")
            .AppendLine("Disallow: /download.html")
            .AppendLine("Disallow: /success.html")
            .AppendLine("Disallow: /cart.html")
            .AppendLine("Disallow: /checkout.html")
            .AppendLine("Disallow: /account.html")
            .AppendLine("Disallow: /library.html")
            .AppendLine("Disallow: /orders.html")
            .AppendLine("Disallow: /profile.html")
            .AppendLine()
            .AppendLine($"Sitemap: {BaseUrl}/sitemap.xml")
            .ToString();

        return Content(body, "text/plain", Encoding.UTF8);
    }

    /// <summary>
    /// Every published book, plus the pages worth indexing. Generated from the
    /// catalogue, so publishing a title puts it in the sitemap and unpublishing takes
    /// it out — no separate thing to remember.
    /// </summary>
    [HttpGet("/sitemap.xml")]
    [Produces("application/xml")]
    public async Task<IActionResult> Sitemap(CancellationToken ct)
    {
        var books = await catalogue.GetBooksAsync(new BookQuery(), ct);
        var categories = await catalogue.GetCategoriesAsync(ct);

        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, Async = false }))
        {
            w.WriteStartDocument();
            w.WriteStartElement("urlset", "http://www.sitemaps.org/schemas/sitemap/0.9");

            WriteUrl(w, BaseUrl + "/", "daily", "1.0");
            WriteUrl(w, BaseUrl + "/shop.html", "daily", "0.9");

            foreach (var c in categories)
                WriteUrl(w, $"{BaseUrl}/shop.html?cat={Uri.EscapeDataString(c.Id)}", "weekly", "0.7");

            foreach (var b in books)
                WriteUrl(w, $"{BaseUrl}/book.html?id={b.Id}", "weekly", "0.8");

            w.WriteEndElement();
            w.WriteEndDocument();
        }

        return Content(sb.ToString(), "application/xml", Encoding.UTF8);
    }

    private static void WriteUrl(XmlWriter w, string loc, string changefreq, string priority)
    {
        w.WriteStartElement("url");
        w.WriteElementString("loc", loc);
        w.WriteElementString("changefreq", changefreq);
        w.WriteElementString("priority", priority);
        w.WriteEndElement();
    }
}
