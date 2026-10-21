using System.Globalization;

namespace Nexus.Infrastructure.Delivery;

/// <summary>
/// The forensic line burned into every page.
/// <para>
/// <b>Latin script and Western digits only, deliberately.</b> PDFsharp draws glyph runs;
/// it does no Arabic shaping and no bidi, so Arabic drawn naively comes out as
/// disconnected left-to-right letterforms — worse than absent. The buyer's name is
/// therefore stamped only when it is Latin-script, and the email, which identifies them
/// just as well, is always stamped.
/// </para>
/// <para>
/// This is not a breach of "Arabic is a first-class edition": the stamp is metadata
/// inside a file, not interface copy. Every string the customer <i>reads</i> — the
/// landing page, the library, the error states, the email — ships in both languages.
/// </para>
/// </summary>
public static class StampText
{
    public static string Footer(string? buyerName, string buyerEmail, string orderNumber, DateTimeOffset at)
    {
        var date = at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var who = IsLatinScript(buyerName) ? $"{buyerName!.Trim()} · " : "";
        return $"Licensed to {who}{buyerEmail} · Order {orderNumber} · {date}";
    }

    /// <summary>
    /// True when every letter is Latin. A name with one Arabic letter in it is not
    /// drawable here, and half a name is worse than none.
    /// </summary>
    public static bool IsLatinScript(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var sawLetter = false;
        foreach (var ch in text)
        {
            if (!char.IsLetter(ch)) continue;
            sawLetter = true;
            if (ch > 0x024F) return false;   // past Latin Extended-B
        }
        return sawLetter;
    }
}
