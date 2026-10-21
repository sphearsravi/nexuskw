using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Domain;
using Nexus.Infrastructure.Email;

namespace Nexus.UnitTests.Email;

public class EmailComposerTests
{
    private static readonly DateTimeOffset Paid = new(2026, 10, 21, 9, 15, 0, TimeSpan.FromHours(3));
    private static readonly DateTimeOffset Expires = new(2026, 10, 28, 9, 15, 0, TimeSpan.FromHours(3));

    private const string Base = "https://nexus.example";
    private const string Token = "7F3K2M9QX1VB4N6ZPR8TKD";

    private static EmailModel Model(
        string language, int items = 1, int links = 1, int maxDownloads = 3, int used = 0) =>
        new(
            StoreName: "Nexus",
            Language: language,
            OrderNumber: "KW-261021-7F3K2M",
            OrderDate: Paid,
            CustomerName: language == "ar" ? "نورة العلي" : "Noura Al-Ali",
            CustomerEmail: "noura@example.com",
            TotalKwd: 6.5m,
            RefundedKwd: 6.5m,
            Items: [.. Enumerable.Range(0, items).Select(i => new EmailItem(
                language == "ar" ? $"المحفظة الصبورة {i}" : $"The Patient Portfolio {i}", 6.5m))],
            Links: [.. Enumerable.Range(0, links).Select(i => new EmailLink(
                language == "ar" ? $"المحفظة الصبورة {i}" : $"The Patient Portfolio {i}",
                Base + EmailTemplateRenderer.DownloadPathPrefix + Token + i,
                Expires, maxDownloads, maxDownloads - used))],
            LinkExpiryDays: 7,
            MaxDownloads: maxDownloads,
            LibraryUrl: Base + "/library.html",
            SupportEmail: "support@nexus.example",
            HasHeldItem: items > links);

    private static Nexus.Application.Email.RenderedEmail Confirmation(string language, EmailModel model)
    {
        var (_, subjectDefault, _, bodyDefault) = EmailTemplates.For(EmailKind.OrderConfirmation, language);
        return EmailComposer.Compose(
            EmailKind.OrderConfirmation, subjectDefault, bodyDefault, model, NullLogger.Instance);
    }

    // ---- money -------------------------------------------------------------

    [Fact]
    public void English_money_is_KWD_at_three_decimals_in_both_parts()
    {
        var email = Confirmation("en", Model("en"));

        Assert.Contains("KD 6.500", email.Text, StringComparison.Ordinal);
        Assert.Contains("KD 6.500", email.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("6.50 ", email.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("KD 6.5<", email.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Arabic_money_is_KWD_at_three_decimals_in_both_parts()
    {
        var email = Confirmation("ar", Model("ar"));

        Assert.Contains("6.500 د.ك", email.Text, StringComparison.Ordinal);
        Assert.Contains("6.500 د.ك", email.Html, StringComparison.Ordinal);
    }

    // ---- digits ------------------------------------------------------------

    // The Phase 1 "PDF، ٦٫٢ ميغابايت" defect, generalised so it cannot come back: one
    // assertion over every template, both kinds, both languages, all three parts.
    [Theory]
    [InlineData("en", EmailKind.OrderConfirmation)]
    [InlineData("ar", EmailKind.OrderConfirmation)]
    [InlineData("en", EmailKind.DownloadLinks)]
    [InlineData("ar", EmailKind.DownloadLinks)]
    [InlineData("en", EmailKind.RefundNotice)]
    [InlineData("ar", EmailKind.RefundNotice)]
    public void No_Arabic_Indic_digit_appears_anywhere(string language, EmailKind kind)
    {
        var (_, subject, _, body) = EmailTemplates.For(kind, language);
        var email = EmailComposer.Compose(kind, subject, body, Model(language), NullLogger.Instance);

        Assert.False(ArabicDigits.Any(email.Subject), email.Subject);
        Assert.False(ArabicDigits.Any(email.Text), new string([.. ArabicDigits.In(email.Text)]));
        Assert.False(ArabicDigits.Any(email.Html), new string([.. ArabicDigits.In(email.Html)]));
    }

    // ---- bidi --------------------------------------------------------------

    /// <summary>
    /// A run containing an ASCII letter is reordered by the bidirectional algorithm when it
    /// sits next to Arabic, so it must be inside a first-strong isolate. Bare numerals are
    /// exempt: the algorithm already renders a weak digit run correctly in an RTL
    /// paragraph, and wrapping them would add noise for no gain.
    /// </summary>
    private static void AssertEveryLatinRunIsolated(string value, string what)
    {
        foreach (Match run in Regex.Matches(value, @"[A-Za-z0-9@._:/?=&%+-]+"))
        {
            if (!run.Value.Any(char.IsAsciiLetter)) continue;

            var before = run.Index > 0 ? value[run.Index - 1] : '\0';
            var after = run.Index + run.Length < value.Length ? value[run.Index + run.Length] : '\0';

            Assert.True(
                before == Bidi.FirstStrongIsolate && after == Bidi.PopDirectionalIsolate,
                $"{what}: the run '{run.Value}' is not bidi-isolated.");
        }
    }

    [Theory]
    [InlineData(EmailKind.OrderConfirmation)]
    [InlineData(EmailKind.DownloadLinks)]
    [InlineData(EmailKind.RefundNotice)]
    public void Arabic_subject_and_text_isolate_every_Latin_run(EmailKind kind)
    {
        var (_, subject, _, body) = EmailTemplates.For(kind, "ar");
        var email = EmailComposer.Compose(kind, subject, body, Model("ar"), NullLogger.Instance);

        AssertEveryLatinRunIsolated(email.Subject, "subject");
        AssertEveryLatinRunIsolated(email.Text, "text part");
    }

    [Fact]
    public void Arabic_html_isolates_the_order_number_with_markup()
    {
        // The resend template is the one that names the order number in its body; the
        // confirmation names it only in the subject, which the text isolates cover.
        var (_, subject, _, body) = EmailTemplates.For(EmailKind.DownloadLinks, "ar");
        var email = EmailComposer.Compose(
            EmailKind.DownloadLinks, subject, body, Model("ar"), NullLogger.Instance);

        Assert.Contains(
            "<span dir=\"ltr\" style=\"unicode-bidi:isolate\">KW-261021-7F3K2M</span>",
            email.Html, StringComparison.Ordinal);
        Assert.Contains("dir=\"rtl\"", email.Html, StringComparison.Ordinal);
        Assert.Contains("lang=\"ar\"", email.Html, StringComparison.Ordinal);
    }

    // Belt as well as the isolation braces: a download URL has no surrounding sentence for
    // any mail client's bidi pass to mangle it against. The library link in the closing
    // paragraph is prose and is isolated instead.
    [Fact]
    public void Every_download_url_in_the_Arabic_text_part_sits_alone_on_its_line()
    {
        var email = Confirmation("ar", Model("ar", items: 2, links: 2));

        var urlLines = email.Text
            .Split('\n')
            .Where(line => line.Contains("download.html", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, urlLines.Count);
        foreach (var line in urlLines)
        {
            var bare = line.Trim().Trim(Bidi.FirstStrongIsolate, Bidi.PopDirectionalIsolate);
            Assert.StartsWith(Base + "/download.html?t=", bare, StringComparison.Ordinal);
            Assert.Equal(Bidi.IsolateText(bare), line.Trim());
        }
    }

    // ---- links -------------------------------------------------------------

    // The emailed URL is the landing page, never /download/{token}: SafeLinks and other
    // scanners follow links, and a scanner must not be able to spend a download.
    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void The_link_is_the_landing_page_not_the_download_route(string language)
    {
        var email = Confirmation(language, Model(language));

        Assert.Contains(Base + "/download.html?t=" + Token, email.Text, StringComparison.Ordinal);
        Assert.Contains(Base + "/download.html?t=" + Token, email.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("/download/" + Token, email.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refund_notice_carries_no_download_link()
    {
        var (_, subject, _, body) = EmailTemplates.For(EmailKind.RefundNotice, "en");
        var email = EmailComposer.Compose(
            EmailKind.RefundNotice, subject, body, Model("en"), NullLogger.Instance);

        Assert.DoesNotContain("download.html", email.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("download.html", email.Html, StringComparison.Ordinal);
        Assert.Contains("KD 6.500", email.Text, StringComparison.Ordinal);
    }

    // ---- the operator's template -------------------------------------------

    [Fact]
    public void An_unknown_token_survives_verbatim_rather_than_throwing()
    {
        var email = EmailComposer.Compose(
            EmailKind.OrderConfirmation, "Subject", "Hello {{customer_name}} {{not_a_token}}",
            Model("en"), NullLogger.Instance);

        Assert.Contains("{{not_a_token}}", email.Text, StringComparison.Ordinal);
        Assert.Contains("Noura Al-Ali", email.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Markup_in_an_operator_template_is_encoded_not_executed()
    {
        var email = EmailComposer.Compose(
            EmailKind.OrderConfirmation, "Subject", "<script>alert(1)</script> {{order_number}}",
            Model("en"), NullLogger.Instance);

        Assert.DoesNotContain("<script>", email.Html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", email.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_buyers_name_containing_markup_is_encoded()
    {
        var model = Model("en") with { CustomerName = "<b>Bobby</b>" };
        var email = Confirmation("en", model);

        Assert.DoesNotContain("<b>Bobby</b>", email.Html, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;Bobby&lt;/b&gt;", email.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Emphasis_becomes_strong_in_html_and_plain_words_in_text()
    {
        var email = EmailComposer.Compose(
            EmailKind.OrderConfirmation, "Subject", "**Nothing has been charged.**",
            Model("en"), NullLogger.Instance);

        Assert.Contains("<strong>Nothing has been charged.</strong>", email.Html, StringComparison.Ordinal);
        Assert.Contains("Nothing has been charged.", email.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("**", email.Text, StringComparison.Ordinal);
    }

    // ---- the held title ----------------------------------------------------

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void A_withheld_title_is_explained_in_the_buyers_language(string language)
    {
        var email = Confirmation(language, Model(language, items: 2, links: 1));
        var expected = language == "ar" ? EmailTemplates.HeldNoticeAr : EmailTemplates.HeldNoticeEn;

        Assert.Contains(expected, email.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void An_ordinary_order_says_nothing_about_held_titles(string language)
    {
        var email = Confirmation(language, Model(language, items: 2, links: 2));
        var notice = language == "ar" ? EmailTemplates.HeldNoticeAr : EmailTemplates.HeldNoticeEn;

        Assert.DoesNotContain(notice, email.Text, StringComparison.Ordinal);
        // and no empty paragraph is left where the notice would have been
        Assert.DoesNotContain("\n\n\n", email.Text, StringComparison.Ordinal);
    }

    // ---- plurals in context ------------------------------------------------

    [Theory]
    [InlineData(1, "يوم واحد")]
    [InlineData(2, "يومان")]
    [InlineData(7, "أيام")]
    [InlineData(30, "يوماً")]
    public void The_Arabic_expiry_phrase_agrees_with_the_number(int days, string expected)
    {
        var model = Model("ar") with { LinkExpiryDays = days };
        var email = Confirmation("ar", model);

        Assert.Contains(expected, email.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_parts_are_produced_and_neither_is_empty()
    {
        var email = Confirmation("ar", Model("ar"));

        Assert.False(string.IsNullOrWhiteSpace(email.Subject));
        Assert.False(string.IsNullOrWhiteSpace(email.Text));
        Assert.Contains("<!DOCTYPE html>", email.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_subject_is_one_line()
    {
        var email = Confirmation("en", Model("en"));

        Assert.DoesNotContain('\n', email.Subject);
        Assert.DoesNotContain('\r', email.Subject);
        Assert.Contains("KW-261021-7F3K2M", email.Subject, StringComparison.Ordinal);
    }
}
