using Nexus.Domain;
using Nexus.Infrastructure.Email;

namespace Nexus.Infrastructure.Accounts;

/// <summary>What an account email says, in both languages.</summary>
/// <param name="Subject">The <c>Subject:</c> line.</param>
/// <param name="Heading">First line of the body.</param>
/// <param name="Lead">Why the message arrived.</param>
/// <param name="ButtonLabel">The call to action.</param>
/// <param name="Expiry">How long the link is good for.</param>
/// <param name="Ignore">What to do if you did not ask for this. Never omitted.</param>
public sealed record AccountEmailCopy(
    string Subject, string Heading, string Lead, string ButtonLabel, string Expiry, string Ignore);

/// <summary>
/// The copy for the two account emails.
/// <para>
/// <b>Not operator-editable, unlike the order emails.</b> The order confirmation is
/// marketing-adjacent prose an operator legitimately wants to reword; these two carry a
/// credential-bearing link, and a template edit that drops the link or the "ignore this"
/// sentence turns a security email into a phishing lesson. The prose is the
/// application's.
/// </para>
/// </summary>
public static class AccountEmailText
{
    public static AccountEmailCopy For(EmailKind kind, string language, string storeName) =>
        (kind, Lang.IsRtl(language)) switch
        {
            (EmailKind.AccountConfirm, false) => new AccountEmailCopy(
                Subject: $"Confirm your email address — {storeName}",
                Heading: "Confirm your email address",
                Lead: $"Someone asked to create a {storeName} account with this address. " +
                      "Confirm it to finish, and any books you have already bought with this " +
                      "address will appear in your library.",
                ButtonLabel: "Confirm my address",
                Expiry: "This link stops working after one day.",
                Ignore: "If this was not you, ignore this message. No account is usable until " +
                        "the address is confirmed, and nobody can see your purchases in the meantime."),

            (EmailKind.AccountConfirm, true) => new AccountEmailCopy(
                Subject: $"أكّد بريدك الإلكتروني — {storeName}",
                Heading: "أكّد بريدك الإلكتروني",
                Lead: $"طلب أحدهم إنشاء حساب في {storeName} بهذا العنوان. " +
                      "أكّد العنوان لإتمام التسجيل، وستظهر في مكتبتك الكتب التي سبق أن اشتريتها بهذا العنوان.",
                ButtonLabel: "تأكيد العنوان",
                Expiry: "ينتهي عمل هذا الرابط بعد يوم واحد.",
                Ignore: "إن لم تكن أنت من طلب ذلك، تجاهل هذه الرسالة. لا يعمل أي حساب قبل تأكيد العنوان، " +
                        "ولا يستطيع أحد الاطلاع على مشترياتك قبل ذلك."),

            (_, false) => new AccountEmailCopy(
                Subject: $"Reset your password — {storeName}",
                Heading: "Reset your password",
                Lead: $"Someone asked to reset the password on the {storeName} account using this address.",
                ButtonLabel: "Choose a new password",
                Expiry: "This link stops working after one day.",
                Ignore: "If this was not you, ignore this message. Your password has not changed."),

            (_, true) => new AccountEmailCopy(
                Subject: $"إعادة تعيين كلمة المرور — {storeName}",
                Heading: "إعادة تعيين كلمة المرور",
                Lead: $"طلب أحدهم إعادة تعيين كلمة المرور لحساب {storeName} المرتبط بهذا العنوان.",
                ButtonLabel: "اختر كلمة مرور جديدة",
                Expiry: "ينتهي عمل هذا الرابط بعد يوم واحد.",
                Ignore: "إن لم تكن أنت من طلب ذلك، تجاهل هذه الرسالة. لم تتغيّر كلمة مرورك.")
        };

    /// <summary>The storefront page that takes the token out of the fragment and uses it.</summary>
    public static string LandingPage(EmailKind kind) =>
        kind == EmailKind.AccountConfirm ? "/confirm.html" : "/reset.html";

    /// <summary>
    /// The token travels in the <b>fragment</b>, which a browser never puts in a request
    /// line. A mailed link therefore leaves no credential in anyone's access log, and the
    /// same choice already governs <c>download.html</c>.
    /// </summary>
    public static string Link(string baseUrl, EmailKind kind, string userId, string token) =>
        $"{baseUrl}{LandingPage(kind)}#u={Uri.EscapeDataString(userId)}&t={AccountTokens.Encode(token)}";
}
