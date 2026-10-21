using System.Globalization;
using System.Net;
using System.Text;

namespace Nexus.Infrastructure.Email;

/// <summary>
/// The HTML scaffold. Deliberately <b>not</b> a Setting: an operator who can edit the
/// layout can break Outlook, and nobody finds out until a customer says the email looked
/// wrong. The operator edits the prose; the application owns the markup.
/// </summary>
public static class EmailHtmlLayout
{
    private const string Font = "'Segoe UI', Tahoma, Arial, Helvetica, sans-serif";
    private const string Ink = "#1b1b1f";
    private const string Muted = "#5c5f6b";
    private const string Rule = "#e3e5ea";
    private const string Accent = "#0b6b5b";

    public static string Encode(string? value) => WebUtility.HtmlEncode(value ?? "");

    /// <summary>
    /// Wraps a rendered body. Direction is set on the document <b>and</b> on the table and
    /// every cell: several Outlook versions ignore document-level <c>dir</c>, and per-cell
    /// is the thing that actually works.
    /// </summary>
    public static string Wrap(string language, string title, string bodyHtml)
    {
        var rtl = Lang.IsRtl(language);
        var dir = rtl ? "rtl" : "ltr";
        var align = rtl ? "right" : "left";

        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\n");
        html.Append($"<html dir=\"{dir}\" lang=\"{language}\"><head>");
        html.Append("<meta charset=\"utf-8\">");
        html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.Append($"<title>{Encode(title)}</title>");
        html.Append("</head>");
        html.Append($"<body dir=\"{dir}\" style=\"margin:0;padding:0;background:#f4f5f7;\">");

        html.Append($"<table role=\"presentation\" dir=\"{dir}\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" " +
                    "border=\"0\" style=\"background:#f4f5f7;\"><tr>");
        html.Append($"<td dir=\"{dir}\" align=\"center\" style=\"padding:24px 12px;\">");

        html.Append($"<table role=\"presentation\" dir=\"{dir}\" width=\"600\" cellpadding=\"0\" cellspacing=\"0\" " +
                    "border=\"0\" style=\"max-width:600px;width:100%;background:#ffffff;" +
                    $"border:1px solid {Rule};border-radius:8px;\">");

        html.Append($"<tr><td dir=\"{dir}\" align=\"{align}\" style=\"padding:24px 28px 8px;font-family:{Font};" +
                    $"font-size:16px;line-height:1.7;color:{Ink};text-align:{align};\">");
        html.Append(bodyHtml);
        html.Append("</td></tr>");

        html.Append($"<tr><td dir=\"{dir}\" align=\"{align}\" style=\"padding:8px 28px 24px;font-family:{Font};" +
                    $"font-size:13px;line-height:1.6;color:{Muted};text-align:{align};border-top:1px solid {Rule};\">");
        html.Append(Encode(rtl
            ? "هذه رسالة آلية تخص طلبك."
            : "This is an automatic message about your order."));
        html.Append("</td></tr>");

        html.Append("</table></td></tr></table></body></html>");

        return html.ToString();
    }

    public static string Paragraph(string language, string inner)
    {
        var rtl = Lang.IsRtl(language);
        return $"<p dir=\"{(rtl ? "rtl" : "ltr")}\" style=\"margin:0 0 16px;text-align:{(rtl ? "right" : "left")};\">{inner}</p>";
    }

    /// <summary>The purchased lines: title on one side, price on the other.</summary>
    public static string ItemTable(EmailModel model)
    {
        var rtl = model.IsRtl;
        var dir = rtl ? "rtl" : "ltr";
        var start = rtl ? "right" : "left";
        var end = rtl ? "left" : "right";

        var html = new StringBuilder();
        html.Append($"<table role=\"presentation\" dir=\"{dir}\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" " +
                    "border=\"0\" style=\"margin:0 0 16px;\">");

        foreach (var item in model.Items)
        {
            html.Append("<tr>");
            html.Append($"<td dir=\"{dir}\" align=\"{start}\" style=\"padding:6px 0;font-family:{Font};font-size:15px;" +
                        $"color:{Ink};text-align:{start};border-bottom:1px solid {Rule};\">" +
                        $"{AutoIsolated(model.Language, item.Title)}</td>");
            // Not ltr-isolated: "6.500 د.ك" must keep the paragraph's direction, or the
            // currency word lands on the wrong side of the amount for an Arabic reader.
            html.Append($"<td dir=\"{dir}\" align=\"{end}\" style=\"padding:6px 0;font-family:{Font};font-size:15px;" +
                        $"color:{Ink};text-align:{end};border-bottom:1px solid {Rule};white-space:nowrap;\">" +
                        $"{Encode(model.Money(item.UnitPriceKwd))}</td>");
            html.Append("</tr>");
        }

        html.Append("</table>");
        return html.ToString();
    }

    /// <summary>
    /// One button and one bare URL per book. The bare URL matters: some Gulf corporate
    /// gateways rewrite or strip anchors, and a link the customer can copy is the fallback.
    /// </summary>
    public static string LinkBlock(EmailModel model)
    {
        var rtl = model.IsRtl;
        var dir = rtl ? "rtl" : "ltr";
        var align = rtl ? "right" : "left";
        var download = rtl ? "تحميل" : "Download";

        var html = new StringBuilder();

        foreach (var link in model.Links)
        {
            var url = Encode(link.Url);

            html.Append($"<table role=\"presentation\" dir=\"{dir}\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" " +
                        $"border=\"0\" style=\"margin:0 0 20px;border:1px solid {Rule};border-radius:6px;\"><tr>");
            html.Append($"<td dir=\"{dir}\" align=\"{align}\" style=\"padding:16px;font-family:{Font};text-align:{align};\">");

            html.Append($"<div style=\"font-size:15px;font-weight:600;color:{Ink};margin:0 0 10px;\">" +
                        $"{AutoIsolated(model.Language, link.Title)}</div>");

            html.Append($"<div style=\"margin:0 0 10px;\"><a href=\"{url}\" " +
                        $"style=\"display:inline-block;padding:10px 20px;background:{Accent};color:#ffffff;" +
                        $"text-decoration:none;border-radius:4px;font-size:15px;\">{Encode(download)}</a></div>");

            html.Append($"<div style=\"font-size:12px;line-height:1.6;color:{Muted};word-break:break-all;\">" +
                        $"{Bidi.IsolateHtml($"<a href=\"{url}\" style=\"color:{Muted};\">{url}</a>")}</div>");

            // U+2068/U+2069 survive HTML encoding untouched, so the mixed Arabic-and-Latin
            // detail line can be isolated as text and then encoded in one piece.
            var remaining = link.Remaining.ToString(CultureInfo.InvariantCulture);
            var detail = rtl
                ? $"ينتهي في {Bidi.IsolateText(model.Date(link.ExpiresAt))} · " +
                  $"{QuantityPhrase.Downloads(link.Remaining, model.Language, Bidi.IsolateText(remaining))} متبقية"
                : $"Expires {model.Date(link.ExpiresAt)} · " +
                  $"{QuantityPhrase.Downloads(link.Remaining, model.Language, remaining)} left";

            html.Append($"<div style=\"margin:10px 0 0;font-size:12px;color:{Muted};\">{Encode(detail)}</div>");

            html.Append("</td></tr></table>");
        }

        return html.ToString();
    }

    /// <summary>A value known to be Latin — a URL, an order number — inside Arabic prose.</summary>
    public static string Isolated(string language, string rawValue) =>
        Lang.IsRtl(language) ? Bidi.IsolateHtml(Encode(rawValue)) : Encode(rawValue);

    /// <summary>A value whose direction is the buyer's or the author's choice, not ours.</summary>
    public static string AutoIsolated(string language, string rawValue) =>
        Lang.IsRtl(language) ? Bidi.AutoIsolateHtml(Encode(rawValue)) : Encode(rawValue);
}
