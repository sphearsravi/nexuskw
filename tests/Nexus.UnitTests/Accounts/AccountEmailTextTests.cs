using Nexus.Domain;
using Nexus.Infrastructure.Accounts;

namespace Nexus.UnitTests.Accounts;

/// <summary>
/// Arabic is a first-class edition, so the two account emails are checked the way the
/// storefront audit checks the interface: both languages present, no Arabic-Indic digits,
/// and no sentence silently shared between editions.
/// </summary>
public sealed class AccountEmailTextTests
{
    public static TheoryData<EmailKind, string> Everything => new()
    {
        { EmailKind.AccountConfirm, "en" },
        { EmailKind.AccountConfirm, "ar" },
        { EmailKind.PasswordReset, "en" },
        { EmailKind.PasswordReset, "ar" }
    };

    [Theory]
    [MemberData(nameof(Everything))]
    public void Every_field_is_present_in_both_languages(EmailKind kind, string language)
    {
        var copy = AccountEmailText.For(kind, language, "Nexus");

        Assert.All(
            new[] { copy.Subject, copy.Heading, copy.Lead, copy.ButtonLabel, copy.Expiry, copy.Ignore },
            value => Assert.False(string.IsNullOrWhiteSpace(value)));
    }

    [Theory]
    [MemberData(nameof(Everything))]
    public void No_string_carries_arabic_indic_digits(EmailKind kind, string language)
    {
        var copy = AccountEmailText.For(kind, language, "Nexus");
        var all = string.Join(' ', copy.Subject, copy.Heading, copy.Lead, copy.ButtonLabel, copy.Expiry, copy.Ignore);

        // PRD §5 forbids them outright: it matches Gulf banking interfaces and keeps
        // what a gateway receives identical whichever language the buyer read.
        Assert.DoesNotMatch("[٠-٩۰-۹]", all);
    }

    [Theory]
    [InlineData(EmailKind.AccountConfirm)]
    [InlineData(EmailKind.PasswordReset)]
    public void The_arabic_edition_is_actually_translated(EmailKind kind)
    {
        var en = AccountEmailText.For(kind, "en", "Nexus");
        var ar = AccountEmailText.For(kind, "ar", "Nexus");

        Assert.NotEqual(en.Subject, ar.Subject);
        Assert.NotEqual(en.Lead, ar.Lead);
        Assert.NotEqual(en.ButtonLabel, ar.ButtonLabel);
    }

    [Theory]
    [MemberData(nameof(Everything))]
    public void Every_message_says_what_to_do_if_you_did_not_ask_for_it(EmailKind kind, string language)
    {
        // The sentence that turns an unexpected security email from alarming into
        // ignorable. Checked rather than remembered because it is the first thing a
        // rewrite drops.
        Assert.False(string.IsNullOrWhiteSpace(AccountEmailText.For(kind, language, "Nexus").Ignore));
    }

    [Fact]
    public void The_confirmation_link_puts_the_token_in_the_fragment()
    {
        var link = AccountEmailText.Link("https://nexus.example", EmailKind.AccountConfirm, "user-7", "tok");

        Assert.StartsWith("https://nexus.example/confirm.html#", link, StringComparison.Ordinal);

        // Everything credential-bearing must be after the '#'. A browser never puts a
        // fragment in a request line, so the mailed link leaves no token in any log.
        var query = link[..link.IndexOf('#')];
        Assert.DoesNotContain("tok", query, StringComparison.Ordinal);
        Assert.DoesNotContain("user-7", query, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reset_link_lands_on_the_reset_page()
    {
        var link = AccountEmailText.Link("https://nexus.example", EmailKind.PasswordReset, "user-7", "tok");
        Assert.StartsWith("https://nexus.example/reset.html#", link, StringComparison.Ordinal);
    }

    [Fact]
    public void The_token_in_the_link_is_the_encoded_one()
    {
        const string token = "CfDJ8+a/b==";
        var link = AccountEmailText.Link("https://nexus.example", EmailKind.PasswordReset, "u", token);
        var encoded = link[(link.IndexOf("&t=", StringComparison.Ordinal) + 3)..];

        Assert.Equal(token, AccountTokens.Decode(encoded));
    }

    [Fact]
    public void An_unknown_language_falls_back_to_english_rather_than_to_nothing()
    {
        var copy = AccountEmailText.For(EmailKind.AccountConfirm, "fr", "Nexus");
        Assert.Equal(AccountEmailText.For(EmailKind.AccountConfirm, "en", "Nexus"), copy);
    }

    [Fact]
    public void The_store_name_the_operator_set_is_the_one_in_the_subject()
    {
        var copy = AccountEmailText.For(EmailKind.AccountConfirm, "en", "Kuwait Books");
        Assert.Contains("Kuwait Books", copy.Subject, StringComparison.Ordinal);
    }
}
