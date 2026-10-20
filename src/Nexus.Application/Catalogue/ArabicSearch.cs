using System.Globalization;
using System.Text;

namespace Nexus.Application.Catalogue;

/// <summary>
/// Folds the orthographic variation Arabic readers type past without thinking,
/// so "المحفظه الصبوره" finds "المحفظة الصبورة" (CAT-10).
/// <para>
/// Applied identically to the stored text and the query, so matching is a plain
/// comparison of two folded strings. SQL Server collations do some of this, but
/// not consistently across versions, and folding here keeps the rule visible
/// and testable rather than buried in a collation name.
/// </para>
/// </summary>
public static class ArabicSearch
{
    public static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var sb = new StringBuilder(value.Length);

        foreach (var ch in value.Normalize(NormalizationForm.FormKD))
        {
            var mapped = ch switch
            {
                // alef in all its dressings
                'آ' or 'أ' or 'إ' or 'ٱ' => 'ا', // آ أ إ ٱ → ا
                // yaa / alef maqsura, and the Farsi yeh people paste in
                'ى' or 'ی' => 'ي',                          // ى ی → ي
                // taa marbuta → haa: readers type either at a word's end
                'ة' => 'ه',                                      // ة → ه
                // waw with hamza
                'ؤ' => 'و',                                      // ؤ → و
                // hamza on the line is dropped entirely below
                'ـ' => '\0',                                               // tatweel
                _ => ch
            };

            if (mapped == '\0') continue;

            // Harakat, shadda, sukun and the rest of the combining marks.
            if (CharUnicodeInfo.GetUnicodeCategory(mapped) == UnicodeCategory.NonSpacingMark) continue;

            // Arabic-Indic digits fold to Western so a query either way matches.
            if (mapped >= '٠' && mapped <= '٩') mapped = (char)(mapped - '٠' + '0');
            if (mapped >= '۰' && mapped <= '۹') mapped = (char)(mapped - '۰' + '0');

            sb.Append(char.ToLowerInvariant(mapped));
        }

        // Collapse runs of whitespace so a stray double space does not miss.
        return string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>True when <paramref name="haystack"/> contains the folded needle.</summary>
    public static bool Contains(string? haystack, string foldedNeedle) =>
        foldedNeedle.Length == 0 || Fold(haystack).Contains(foldedNeedle, StringComparison.Ordinal);
}
