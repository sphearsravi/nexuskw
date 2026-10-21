using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Nexus.Application.Email;
using Nexus.Domain;

namespace Nexus.Infrastructure.Email;

/// <summary>
/// Turns a template and a resolved model into a subject, an HTML part and a text part.
/// <para>
/// <b>Templates are restricted-token strings — not Razor, not Liquid, not any engine.</b>
/// An operator-edited string that reaches a compiler is a code-execution path, and in a
/// public repository the shape of that path is published. This does one thing: it replaces
/// known tokens. The operator edits the prose around the blocks; they cannot delete the
/// download links, and they cannot write <c>6.50</c>.
/// </para>
/// <para>
/// No database and no clock, so the whole of the money, bidi and plural behaviour is unit
/// testable against a hand-built model.
/// </para>
/// </summary>
public static partial class EmailComposer
{
    public const string BookListToken = "book_list";
    public const string DownloadLinksToken = "download_links";

    public static RenderedEmail Compose(
        EmailKind kind, string subjectTemplate, string bodyTemplate, EmailModel model, ILogger log)
    {
        var tokens = BuildTokens(model, kind);

        var subject = Collapse(Substitute(subjectTemplate, tokens, html: false, log));
        var (html, text) = RenderBody(bodyTemplate, tokens, model, log);

        return new RenderedEmail(subject, EmailHtmlLayout.Wrap(model.Language, subject, html), text);
    }

    /// <summary>A token's two renderings. Scalars are HTML-encoded; blocks are not.</summary>
    private sealed record Token(string Html, string Text);

    private static Dictionary<string, Token> BuildTokens(EmailModel model, EmailKind kind)
    {
        var lang = model.Language;
        var rtl = model.IsRtl;

        string Number(int n) => n.ToString(CultureInfo.InvariantCulture);
        string IsolatedNumber(int n) => rtl ? Bidi.IsolateText(Number(n)) : Number(n);

        var held = model.HasHeldItem
            ? (rtl ? EmailTemplates.HeldNoticeAr : EmailTemplates.HeldNoticeEn)
            : "";

        var tokens = new Dictionary<string, Token>(StringComparer.Ordinal)
        {
            ["store_name"] = Scalar(lang, model.StoreName, auto: true),
            ["customer_name"] = Scalar(lang, model.CustomerName, auto: true),
            ["customer_email"] = Scalar(lang, model.CustomerEmail, auto: false),
            ["order_number"] = Scalar(lang, model.OrderNumber, auto: false),
            ["order_date"] = Scalar(lang, model.Date(model.OrderDate), auto: true),
            ["order_total"] = Scalar(lang, model.Money(model.TotalKwd), auto: true),
            ["refund_amount"] = Scalar(lang, model.Money(model.RefundedKwd), auto: true),
            ["link_expiry_days"] = Scalar(lang, Number(model.LinkExpiryDays), auto: true),
            ["max_downloads"] = Scalar(lang, Number(model.MaxDownloads), auto: true),
            ["expiry_phrase"] = Scalar(
                lang, QuantityPhrase.Days(model.LinkExpiryDays, lang, IsolatedNumber(model.LinkExpiryDays)), auto: true),
            ["downloads_phrase"] = Scalar(
                lang, QuantityPhrase.Downloads(model.MaxDownloads, lang, IsolatedNumber(model.MaxDownloads)), auto: true),
            ["library_url"] = Scalar(lang, model.LibraryUrl, auto: false),
            ["support_email"] = Scalar(lang, model.SupportEmail, auto: false),
            ["held_notice"] = Scalar(lang, held, auto: true),

            [BookListToken] = new(EmailHtmlLayout.ItemTable(model), EmailTextLayout.ItemList(model)),
            [DownloadLinksToken] = new(EmailHtmlLayout.LinkBlock(model), EmailTextLayout.LinkList(model))
        };

        // A refund notice carries no links. Defining the block as empty rather than leaving
        // it out means an operator who pastes it into the refund template gets nothing,
        // not a list of books they can still download.
        if (kind == EmailKind.RefundNotice) tokens[DownloadLinksToken] = new("", "");

        return tokens;
    }

    private static Token Scalar(string language, string value, bool auto)
    {
        if (string.IsNullOrEmpty(value)) return new("", "");

        var html = auto
            ? EmailHtmlLayout.AutoIsolated(language, value)
            : EmailHtmlLayout.Isolated(language, value);

        var text = Lang.IsRtl(language) && NeedsTextIsolation(value) ? Bidi.IsolateText(value) : value;

        return new(html, text);
    }

    /* A run containing an ASCII letter — a URL, an order number, an address — is reordered
     * next to Arabic and must be isolated. A bare numeral is not: the bidirectional
     * algorithm already treats digits as weak and renders them correctly inside a
     * right-to-left paragraph, so wrapping them is noise. */
    private static bool NeedsTextIsolation(string value) => value.Any(char.IsAsciiLetter);

    private static (string Html, string Text) RenderBody(
        string template, Dictionary<string, Token> tokens, EmailModel model, ILogger log)
    {
        var htmlParts = new List<string>();
        var textParts = new List<string>();

        foreach (var raw in ParagraphBreak().Split(template.Replace("\r\n", "\n")))
        {
            var paragraph = raw.Trim();
            if (paragraph.Length == 0) continue;

            // A paragraph that is nothing but a block token becomes the block: the table of
            // purchased lines, or the download buttons.
            var blockName = WholeToken().Match(paragraph) is { Success: true } m ? m.Groups[1].Value : null;
            if (blockName is BookListToken or DownloadLinksToken && tokens.TryGetValue(blockName, out var block))
            {
                if (block.Html.Length > 0) htmlParts.Add(block.Html);
                if (block.Text.Length > 0) textParts.Add(block.Text);
                continue;
            }

            var html = Substitute(paragraph, tokens, html: true, log);
            var text = Substitute(paragraph, tokens, html: false, log);

            // Drops a paragraph whose tokens all resolved to nothing — the held-title
            // notice on an ordinary order — rather than leaving a gap behind it.
            if (text.Trim().Length == 0) continue;

            htmlParts.Add(EmailHtmlLayout.Paragraph(model.Language, html.Replace("\n", "<br>")));
            textParts.Add(text);
        }

        return (string.Join("\n", htmlParts), string.Join("\n\n", textParts));
    }

    private static string Substitute(string template, Dictionary<string, Token> tokens, bool html, ILogger log)
    {
        var output = new StringBuilder();
        var cursor = 0;

        foreach (Match match in TokenPattern().Matches(template))
        {
            AppendLiteral(output, template[cursor..match.Index], html);
            cursor = match.Index + match.Length;

            if (tokens.TryGetValue(match.Groups[1].Value, out var token))
            {
                output.Append(html ? token.Html : token.Text);
            }
            else
            {
                // Verbatim, never an exception: a typo in an operator's template must not
                // stop a paid customer's email.
                LogUnknownToken(log, match.Value);
                AppendLiteral(output, match.Value, html);
            }
        }

        AppendLiteral(output, template[cursor..], html);
        return output.ToString();
    }

    private static void AppendLiteral(StringBuilder output, string literal, bool html)
    {
        if (literal.Length == 0) return;

        if (!html)
        {
            output.Append(literal.Replace("**", ""));
            return;
        }

        // Encode first, then promote the emphasis markers, so a template cannot inject
        // markup and an operator's asterisk cannot open a tag.
        var encoded = EmailHtmlLayout.Encode(literal);
        output.Append(Emphasis().Replace(encoded, "<strong>$1</strong>").Replace("**", ""));
    }

    private static string Collapse(string value) => Whitespace().Replace(value, " ").Trim();

    [GeneratedRegex(@"\{\{([a-z_]+)\}\}")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"^\{\{([a-z_]+)\}\}$")]
    private static partial Regex WholeToken();

    [GeneratedRegex(@"\n[ \t]*\n")]
    private static partial Regex ParagraphBreak();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"[ \t\r\n]+")]
    private static partial Regex Whitespace();

    [LoggerMessage(EventId = 5101, Level = LogLevel.Warning,
        Message = "Email template contains unknown token {Token}; it was left verbatim.")]
    private static partial void LogUnknownToken(ILogger logger, string token);
}
