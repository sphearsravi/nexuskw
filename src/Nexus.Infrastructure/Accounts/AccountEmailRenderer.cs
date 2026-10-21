using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Nexus.Application.Common;
using Nexus.Application.Email;
using Nexus.Domain;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Email;

namespace Nexus.Infrastructure.Accounts;

/// <summary>
/// Renders the two account emails, and hands everything else to the order renderer it
/// wraps.
/// <para>
/// A decorator rather than an edit to <c>EmailTemplateRenderer</c>: that file belongs to
/// the email stream, this phase owns the two kinds it did not implement, and a wrapper
/// registered in this phase's own DI file keeps the seam where the ownership boundary is.
/// </para>
/// <para>
/// <b>The token is minted here, at dispatch, not at enqueue</b> — which is decision R4
/// applied to a credential rather than to a download link. The outbox stores no body and
/// now stores no token either, so a database backup carries nothing that confirms an
/// address or resets a password, and a message retried after a long queue delay carries a
/// link that is live rather than one that quietly expired in the queue.
/// </para>
/// </summary>
public sealed class AccountEmailRenderer(
    IEmailTemplateRenderer inner,
    UserManager<NexusUser> users,
    ISettingsReader settings,
    IOptions<SiteOptions> site) : IEmailTemplateRenderer
{
    public async Task<(RenderedEmail? Email, string? Problem)> RenderAsync(
        OutboxEmail queued, CancellationToken ct = default)
    {
        if (queued.Kind is not (EmailKind.AccountConfirm or EmailKind.PasswordReset))
            return await inner.RenderAsync(queued, ct);

        /* By address, not by a stored user id: OutboxEmail's columns are frozen and hold
         * an order and a customer, neither of which a registration has yet. The address
         * is the thing the message is about in any case. */
        var user = await users.FindByEmailAsync(queued.ToEmail);
        if (user is null) return (null, "account_not_found");

        // Confirming an address that is already confirmed is not a thing to mail about,
        // and the duplicate would arrive after the customer had already finished.
        if (queued.Kind == EmailKind.AccountConfirm && user.EmailConfirmed)
            return (null, "already_confirmed");

        if (queued.Kind == EmailKind.PasswordReset && !user.EmailConfirmed)
            return (null, "not_confirmed");

        var token = queued.Kind == EmailKind.AccountConfirm
            ? await users.GenerateEmailConfirmationTokenAsync(user)
            : await users.GeneratePasswordResetTokenAsync(user);

        var language = Lang.Normalise(queued.Language);
        var storeName = await settings.GetStringAsync(
            SettingKeys.FromName, EmailTemplates.DefaultFromName, ct);

        var copy = AccountEmailText.For(queued.Kind, language, storeName);
        var url = AccountEmailText.Link(site.Value.BaseOrFallback(), queued.Kind, user.Id, token);

        return (new RenderedEmail(copy.Subject, Html(language, copy, url), Text(copy, url)), null);
    }

    private static string Html(string language, AccountEmailCopy copy, string url)
    {
        var encoded = EmailHtmlLayout.Encode(url);

        var body =
            EmailHtmlLayout.Paragraph(language, $"<strong>{EmailHtmlLayout.Encode(copy.Heading)}</strong>") +
            EmailHtmlLayout.Paragraph(language, EmailHtmlLayout.Encode(copy.Lead)) +
            EmailHtmlLayout.Paragraph(language,
                $"<a href=\"{encoded}\" style=\"display:inline-block;padding:10px 20px;background:#0b6b5b;" +
                $"color:#ffffff;text-decoration:none;border-radius:4px;font-size:15px;\">" +
                $"{EmailHtmlLayout.Encode(copy.ButtonLabel)}</a>") +
            // Some Gulf corporate gateways rewrite or strip the anchor; the bare URL is
            // what the customer falls back to, so it is always printed as well.
            EmailHtmlLayout.Paragraph(language,
                $"<span style=\"font-size:12px;word-break:break-all;color:#5c5f6b;\">" +
                $"{Bidi.IsolateHtml(encoded)}</span>") +
            EmailHtmlLayout.Paragraph(language, EmailHtmlLayout.Encode(copy.Expiry)) +
            EmailHtmlLayout.Paragraph(language, EmailHtmlLayout.Encode(copy.Ignore));

        return EmailHtmlLayout.Wrap(language, copy.Subject, body);
    }

    /// <summary>The URL sits alone on its line so no mail client's bidi pass can reorder it.</summary>
    private static string Text(AccountEmailCopy copy, string url) =>
        $"{copy.Heading}\n\n{copy.Lead}\n\n{Bidi.IsolateText(url)}\n\n{copy.Expiry}\n\n{copy.Ignore}\n";
}
