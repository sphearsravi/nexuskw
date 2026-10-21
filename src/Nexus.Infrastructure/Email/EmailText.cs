using System.Globalization;
using Nexus.Domain;

namespace Nexus.Infrastructure.Email;

/// <summary>
/// Language tokens as the storefront uses them — the same two-letter code addresses the
/// template, the layout direction and the i18n dictionary.
/// </summary>
public static class Lang
{
    public const string En = "en";
    public const string Ar = "ar";

    /// <summary>
    /// Trim, lowercase, two-letter prefix. <c>"AR"</c>, <c>"ar-KW"</c> and <c>"ar "</c> all
    /// resolve to <c>ar</c>; anything else is English. Returns false when the input was not
    /// recognised, so the caller can log it rather than silently answering in the wrong
    /// language to an Arabic buyer.
    /// </summary>
    public static bool TryNormalise(string? raw, out string language)
    {
        var trimmed = (raw ?? "").Trim().ToLowerInvariant();
        var prefix = trimmed.Length >= 2 ? trimmed[..2] : trimmed;

        language = prefix == Ar ? Ar : En;
        return prefix is Ar or En;
    }

    public static string Normalise(string? raw)
    {
        TryNormalise(raw, out var language);
        return language;
    }

    public static bool IsRtl(string language) => language == Ar;
}

/// <summary>
/// Bidirectional isolation.
/// <para>
/// In a right-to-left body the bidirectional algorithm reorders a Latin run next to Arabic
/// text: <c>KD 6.500</c> drifts and <c>KW-261021-7F3K2M</c> can have its hyphens rendered on
/// the wrong side. This is the specific defect that makes an Arabic receipt look amateur,
/// and it is invisible until a native reader opens one. Markup cannot reach into a subject
/// line or a text part, so those use the Unicode isolates instead.
/// </para>
/// </summary>
public static class Bidi
{
    public const char FirstStrongIsolate = '⁨';
    public const char PopDirectionalIsolate = '⁩';

    /// <summary>For a subject line or the plain-text alternative.</summary>
    public static string IsolateText(string value) =>
        string.IsNullOrEmpty(value) ? value : FirstStrongIsolate + value + PopDirectionalIsolate;

    /// <summary>
    /// For the HTML part. Both the attribute and the inline style are needed: several
    /// Outlook versions ignore one or the other.
    /// </summary>
    public static string IsolateHtml(string encodedValue) =>
        string.IsNullOrEmpty(encodedValue)
            ? encodedValue
            : $"<span dir=\"ltr\" style=\"unicode-bidi:isolate\">{encodedValue}</span>";

    /// <summary>
    /// For a value whose own direction is unknown — a book title or a buyer's name, which
    /// may be Arabic or Latin in either edition. <c>dir="auto"</c> takes the direction from
    /// the first strong character, exactly as the text isolate does.
    /// </summary>
    public static string AutoIsolateHtml(string encodedValue) =>
        string.IsNullOrEmpty(encodedValue)
            ? encodedValue
            : $"<span dir=\"auto\" style=\"unicode-bidi:isolate\">{encodedValue}</span>";

    /// <summary>Isolates only in a right-to-left edition; English needs none of this.</summary>
    public static string AutoIsolateText(string language, string value) =>
        Lang.IsRtl(language) ? IsolateText(value) : value;
}

/// <summary>
/// Money as a reader sees it.
/// <para>
/// <see cref="Currency.Format"/> returns the bare <c>6.500</c> and always will — that is
/// what Tap hashes. This is its customer-facing twin, and the one an email uses. Digits
/// stay Western in both languages.
/// </para>
/// </summary>
public static class MoneyText
{
    public static string Format(decimal amountKwd, string language) =>
        Lang.IsRtl(language)
            ? Currency.Kwd.Format(amountKwd) + " د.ك"
            : "KD " + Currency.Kwd.Format(amountKwd);
}

/// <summary>
/// Dates in Kuwait time, in Western digits, with Arabic month names a Kuwaiti reader
/// actually uses.
/// </summary>
public static class KuwaitDates
{
    /* Hardcoded rather than taken from CultureInfo("ar-KW") for two reasons: a generic
     * Arabic culture may produce the Levantine names (كانون الثاني and so on), which is not
     * what a Gulf reader expects, and ICU substitutes Arabic-Indic numerals for some Arabic
     * cultures, which PRD §5 forbids outright. A fixed array cannot regress when the CI
     * runner's ICU version changes. */
    private static readonly string[] ArabicMonths =
    [
        "يناير", "فبراير", "مارس", "أبريل", "مايو", "يونيو",
        "يوليو", "أغسطس", "سبتمبر", "أكتوبر", "نوفمبر", "ديسمبر"
    ];

    private static readonly string[] EnglishMonths =
    [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December"
    ];

    /// <summary>Kuwait is UTC+3 with no daylight saving, so a fixed offset is the honest fallback.</summary>
    public static readonly TimeSpan Offset = TimeSpan.FromHours(3);

    public static DateTimeOffset ToKuwait(DateTimeOffset instant) => instant.ToOffset(Offset);

    /// <summary>e.g. <c>21 October 2026</c> / <c>21 أكتوبر 2026</c>, always Western digits.</summary>
    public static string Format(DateTimeOffset instant, string language)
    {
        var local = ToKuwait(instant);
        var month = Lang.IsRtl(language) ? ArabicMonths[local.Month - 1] : EnglishMonths[local.Month - 1];

        return string.Create(CultureInfo.InvariantCulture, $"{local.Day} {month} {local.Year}");
    }
}

/// <summary>
/// Whole quantity phrases, because <c>{{n}} أيام</c> is only correct for three to ten.
/// <para>
/// The defaults (7 days, 3 downloads) read correctly, which is exactly why a bare
/// <c>{{link_expiry_days}} أيام</c> would have passed review and then broken on the day an
/// operator set the expiry to one day.
/// </para>
/// </summary>
public static class QuantityPhrase
{
    /// <param name="number">
    /// The already-rendered numeral. The caller supplies it isolated for Arabic, so the
    /// isolation markers land around the digits and not around the Arabic words.
    /// </param>
    public static string Days(int n, string language, string number) =>
        Lang.IsRtl(language)
            ? n switch
            {
                1 => "يوم واحد",
                2 => "يومان",
                >= 3 and <= 10 => $"{number} أيام",
                _ => $"{number} يوماً"
            }
            : n == 1 ? $"{number} day" : $"{number} days";

    public static string Downloads(int n, string language, string number) =>
        Lang.IsRtl(language)
            ? n switch
            {
                1 => "عملية تحميل واحدة",
                2 => "عمليتا تحميل",
                >= 3 and <= 10 => $"{number} عمليات تحميل",
                _ => $"{number} عملية تحميل"
            }
            : n == 1 ? $"{number} download" : $"{number} downloads";
}
