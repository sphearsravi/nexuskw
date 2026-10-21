using System.Globalization;
using System.Text;

namespace Nexus.Infrastructure.Email;

/// <summary>
/// The <c>text/plain</c> alternative. It is never optional: some Gulf corporate mail
/// gateways strip HTML entirely, and a download link that exists only in the HTML part is
/// a link the customer never sees.
/// </summary>
public static class EmailTextLayout
{
    public static string ItemList(EmailModel model)
    {
        var text = new StringBuilder();

        foreach (var item in model.Items)
        {
            text.Append("- ")
                .Append(Bidi.AutoIsolateText(model.Language, item.Title))
                .Append(" — ")
                .Append(model.Money(item.UnitPriceKwd))
                .Append('\n');
        }

        return text.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Each URL sits alone on its own line with no surrounding sentence, so no mail
    /// client's bidi pass can mangle it — belt as well as the isolation braces.
    /// </summary>
    public static string LinkList(EmailModel model)
    {
        var rtl = model.IsRtl;
        var text = new StringBuilder();

        foreach (var link in model.Links)
        {
            var remaining = link.Remaining.ToString(CultureInfo.InvariantCulture);
            var phrase = QuantityPhrase.Downloads(
                link.Remaining, model.Language, rtl ? Bidi.IsolateText(remaining) : remaining);

            text.Append(Bidi.AutoIsolateText(model.Language, link.Title)).Append('\n');
            text.Append(Bidi.IsolateText(link.Url)).Append('\n');
            text.Append(rtl
                    ? $"ينتهي في {Bidi.IsolateText(model.Date(link.ExpiresAt))} · {phrase} متبقية"
                    : $"Expires {model.Date(link.ExpiresAt)} · {phrase} left")
                .Append("\n\n");
        }

        return text.ToString().TrimEnd('\n');
    }
}
