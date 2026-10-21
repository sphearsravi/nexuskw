using Nexus.Application.Common;
using Nexus.Domain;

namespace Nexus.Infrastructure.Email;

/// <summary>
/// The <c>email.*</c> keys this stage owns that <see cref="SettingKeys"/> does not already
/// name. Named constants rather than loose strings, so a typo is a compile error instead of
/// a silent fallback to the built-in default.
/// </summary>
public static class EmailSettingKeys
{
    public const string Enabled = "email.enabled";
    public const string ReplyTo = "email.replyTo";
    public const string SupportAddress = "email.supportAddress";

    public const string DownloadLinksSubjectEn = "email.downloadLinks.subject.en";
    public const string DownloadLinksSubjectAr = "email.downloadLinks.subject.ar";
    public const string DownloadLinksBodyEn = "email.downloadLinks.body.en";
    public const string DownloadLinksBodyAr = "email.downloadLinks.body.ar";

    public const string RefundSubjectEn = "email.refund.subject.en";
    public const string RefundSubjectAr = "email.refund.subject.ar";
    public const string RefundBodyEn = "email.refund.body.en";
    public const string RefundBodyAr = "email.refund.body.ar";

    public const string ResendPerOrderPerHour = "email.resend.perOrderPerHour";
    public const string ResendPerActorPerHour = "email.resend.perActorPerHour";
    public const string ResendPerRecipientPerHour = "email.resend.perRecipientPerHour";
    public const string ResendPerRecipientPerDay = "email.resend.perRecipientPerDay";
}

/// <summary>
/// The seeded defaults.
/// <para>
/// They are also the fallback the renderer reads through <see cref="ISettingsReader"/>, so
/// an email is correct on a database where the seeder has not run — but they are seeded all
/// the same, because a row the operator can see and edit from day one beats a magic default
/// they discover does not exist.
/// </para>
/// <para>
/// The Arabic is written as Arabic, not as English with Arabic words: no عميلنا العزيز, no
/// passive constructions imported from the English, and the register a Gulf reader actually
/// uses. A translation reviewer, not a translation tool, signs it off before Phase 9.
/// </para>
/// </summary>
public static class EmailTemplates
{
    public const string DefaultFromAddress = "orders@nexus.example";
    public const string DefaultFromName = "Nexus";
    public const string DefaultSupportAddress = "support@nexus.example";

    public const string ConfirmationSubjectEn = "Your order {{order_number}} — your download link";
    public const string ConfirmationSubjectAr = "طلبك {{order_number}} — رابط التحميل";

    public const string ConfirmationBodyEn = """
        Hello {{customer_name}},

        Thank you. Your payment went through and your book is ready.

        {{book_list}}

        Total {{order_total}} · paid {{order_date}}

        **Download your file**

        {{download_links}}

        {{held_notice}}

        Each link works for {{expiry_phrase}} and up to {{downloads_phrase}} — enough for a phone, a laptop and a mistake.

        Every file carries your name and your order number.

        Keep this email, or sign in at {{library_url}} and your books will be waiting.

        Any problem at all, just reply to this message.
        """;

    public const string ConfirmationBodyAr = """
        مرحباً {{customer_name}}،

        شكراً لك. تم استلام الدفعة، وكتابك جاهز.

        {{book_list}}

        الإجمالي {{order_total}} · تاريخ الدفع {{order_date}}

        **حمّل ملفك**

        {{download_links}}

        {{held_notice}}

        كل رابط يعمل لمدة {{expiry_phrase}}، وحتى {{downloads_phrase}} — ما يكفي للهاتف والحاسوب ومحاولة إضافية.

        يحمل كل ملف اسمك ورقم طلبك.

        احتفظ بهذه الرسالة، أو سجّل الدخول على {{library_url}} وستجد كتبك بانتظارك.

        لأي ملاحظة، يكفي الرد على هذه الرسالة.
        """;

    public const string DownloadLinksSubjectEn = "Your download link for order {{order_number}}";
    public const string DownloadLinksSubjectAr = "رابط التحميل لطلبك {{order_number}}";

    /* The "nothing has been charged" line is not padding. A second email about an order the
     * customer already paid for reads, at a glance, like a second charge, and that is a
     * support call and possibly a chargeback. */
    public const string DownloadLinksBodyEn = """
        Hello {{customer_name}},

        Here is your download link again, as requested.

        {{download_links}}

        **Nothing has been charged.** Order {{order_number}} was paid on {{order_date}} and this message only repeats the link.

        Each link works for {{expiry_phrase}} and up to {{downloads_phrase}}.

        If a link has stopped working, reply and we will issue a new one.
        """;

    public const string DownloadLinksBodyAr = """
        مرحباً {{customer_name}}،

        هذا رابط التحميل مرة أخرى، كما طلبت.

        {{download_links}}

        **لم يتم خصم أي مبلغ.** طلبك {{order_number}} مدفوع بتاريخ {{order_date}}، وهذه الرسالة تعيد إرسال الرابط فقط.

        كل رابط يعمل لمدة {{expiry_phrase}}، وحتى {{downloads_phrase}}.

        إذا توقف أحد الروابط عن العمل، ردّ على الرسالة وسنصدر رابطاً جديداً.
        """;

    public const string RefundSubjectEn = "Refund for order {{order_number}}";
    public const string RefundSubjectAr = "استرداد مبلغ طلبك {{order_number}}";

    public const string RefundBodyEn = """
        Hello {{customer_name}},

        We have refunded {{refund_amount}} on order {{order_number}}, paid {{order_date}}.

        The money goes back the way it came. Your bank usually shows it within a few working days.

        The download links for the refunded titles no longer work.

        If this is not what you expected, reply to this message.
        """;

    public const string RefundBodyAr = """
        مرحباً {{customer_name}}،

        تم استرداد {{refund_amount}} من طلبك {{order_number}}، المدفوع بتاريخ {{order_date}}.

        يعود المبلغ بالطريقة نفسها التي دفعت بها، ويظهر عادةً في كشف حسابك خلال أيام عمل قليلة.

        روابط التحميل للعناوين المستردة لم تعد تعمل.

        إذا لم يكن هذا ما تتوقعه، ردّ على هذه الرسالة.
        """;

    /// <summary>
    /// C-withdrawn: a title unpublished between order and capture is paid for but held. The
    /// buyer is told plainly rather than finding one book missing from a list of two.
    /// </summary>
    public const string HeldNoticeEn =
        "One title on this order is being checked and is not in the list above. We will be in touch about it, and you have not lost it.";

    public const string HeldNoticeAr =
        "أحد العناوين في هذا الطلب قيد المراجعة ولم يظهر في القائمة أعلاه. سنتواصل معك بشأنه، ولم تفقده.";

    /// <summary>Every seeded key with its default, for the renderer and for the seeder.</summary>
    public static IReadOnlyDictionary<string, string> Defaults { get; } = new Dictionary<string, string>
    {
        [EmailSettingKeys.Enabled] = "true",
        [SettingKeys.FromAddress] = DefaultFromAddress,
        [SettingKeys.FromName] = DefaultFromName,
        [EmailSettingKeys.ReplyTo] = "",
        [EmailSettingKeys.SupportAddress] = DefaultSupportAddress,

        [SettingKeys.ConfirmationSubjectEn] = ConfirmationSubjectEn,
        [SettingKeys.ConfirmationSubjectAr] = ConfirmationSubjectAr,
        [SettingKeys.ConfirmationBodyEn] = ConfirmationBodyEn,
        [SettingKeys.ConfirmationBodyAr] = ConfirmationBodyAr,

        [EmailSettingKeys.DownloadLinksSubjectEn] = DownloadLinksSubjectEn,
        [EmailSettingKeys.DownloadLinksSubjectAr] = DownloadLinksSubjectAr,
        [EmailSettingKeys.DownloadLinksBodyEn] = DownloadLinksBodyEn,
        [EmailSettingKeys.DownloadLinksBodyAr] = DownloadLinksBodyAr,

        [EmailSettingKeys.RefundSubjectEn] = RefundSubjectEn,
        [EmailSettingKeys.RefundSubjectAr] = RefundSubjectAr,
        [EmailSettingKeys.RefundBodyEn] = RefundBodyEn,
        [EmailSettingKeys.RefundBodyAr] = RefundBodyAr,

        [SettingKeys.ResendLimitPerDay] = "10",
        [EmailSettingKeys.ResendPerOrderPerHour] = "3",
        [EmailSettingKeys.ResendPerActorPerHour] = "30",
        [EmailSettingKeys.ResendPerRecipientPerHour] = "5",
        [EmailSettingKeys.ResendPerRecipientPerDay] = "20"
    };

    /// <summary>The subject and body keys, and their defaults, for one kind and language.</summary>
    public static (string SubjectKey, string SubjectDefault, string BodyKey, string BodyDefault)
        For(EmailKind kind, string language)
    {
        var ar = Lang.IsRtl(language);

        return kind switch
        {
            EmailKind.OrderConfirmation => ar
                ? (SettingKeys.ConfirmationSubjectAr, ConfirmationSubjectAr, SettingKeys.ConfirmationBodyAr, ConfirmationBodyAr)
                : (SettingKeys.ConfirmationSubjectEn, ConfirmationSubjectEn, SettingKeys.ConfirmationBodyEn, ConfirmationBodyEn),

            EmailKind.DownloadLinks => ar
                ? (EmailSettingKeys.DownloadLinksSubjectAr, DownloadLinksSubjectAr, EmailSettingKeys.DownloadLinksBodyAr, DownloadLinksBodyAr)
                : (EmailSettingKeys.DownloadLinksSubjectEn, DownloadLinksSubjectEn, EmailSettingKeys.DownloadLinksBodyEn, DownloadLinksBodyEn),

            EmailKind.RefundNotice => ar
                ? (EmailSettingKeys.RefundSubjectAr, RefundSubjectAr, EmailSettingKeys.RefundBodyAr, RefundBodyAr)
                : (EmailSettingKeys.RefundSubjectEn, RefundSubjectEn, EmailSettingKeys.RefundBodyEn, RefundBodyEn),

            _ => throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "Phase 6 owns the account kinds; they have no template here yet.")
        };
    }

    /// <summary>
    /// Kinds whose whole purpose is the link. Rendering one without a live grant is refused
    /// rather than sent: a customer who tries a dead link distrusts the next one too.
    /// </summary>
    public static bool RequiresLiveGrants(EmailKind kind) =>
        kind is EmailKind.OrderConfirmation or EmailKind.DownloadLinks;
}
