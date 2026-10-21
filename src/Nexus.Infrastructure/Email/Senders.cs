using System.Globalization;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Nexus.Application.Common;
using Nexus.Application.Email;

namespace Nexus.Infrastructure.Email;

/// <summary>Who the message is from. Content and policy, so it lives in Settings.</summary>
public sealed record EmailFrom(string Address, string Name, string? ReplyTo);

/// <summary>
/// Resolves the visible sender once, for the dispatcher (which needs the domain to build
/// the envelope return path) and for the transport (which needs the header).
/// </summary>
public sealed class EmailIdentity(ISettingsReader settings)
{
    public async Task<EmailFrom> ResolveAsync(CancellationToken ct = default)
    {
        var address = await settings.GetStringAsync(
            SettingKeys.FromAddress, EmailTemplates.DefaultFromAddress, ct);
        var name = await settings.GetStringAsync(
            SettingKeys.FromName, EmailTemplates.DefaultFromName, ct);
        var replyTo = await settings.GetStringAsync(EmailSettingKeys.ReplyTo, "", ct);

        return new EmailFrom(address, name, string.IsNullOrWhiteSpace(replyTo) ? null : replyTo);
    }
}

/// <summary>
/// Turns an <see cref="OutgoingEmail"/> into a MIME message.
/// <para>
/// Always <c>multipart/alternative</c> with a plain-text part. Some Gulf corporate mail
/// gateways strip HTML entirely, and a download link that exists only in the HTML part is
/// a link the customer never sees. UTF-8 throughout — a fallback to ISO-8859-1 is how
/// Arabic becomes <c>????</c>.
/// </para>
/// </summary>
public static class MimeFactory
{
    public static MimeMessage Build(OutgoingEmail message, EmailFrom from)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(from.Name, from.Address));
        mime.To.Add(new MailboxAddress(message.ToName, message.ToEmail));
        mime.Subject = message.Subject;
        mime.MessageId = message.MessageId;

        if (!string.IsNullOrWhiteSpace(from.ReplyTo))
            mime.ReplyTo.Add(MailboxAddress.Parse(from.ReplyTo));

        mime.Body = new BodyBuilder { HtmlBody = message.Html, TextBody = message.Text }.ToMessageBody();
        return mime;
    }
}

/// <summary>
/// The transport that opens no socket. The outbox row is still written and still visible,
/// marked Suppressed — which is the correct state until an SMTP provider is chosen, and is
/// not the same thing as silence.
/// </summary>
public sealed class NullEmailSender : IEmailSender
{
    public Task<EmailSendResult> SendAsync(OutgoingEmail message, CancellationToken ct = default) =>
        Task.FromResult(new EmailSendResult(
            EmailSendOutcome.PermanentFailure, "Email:Transport is None; nothing was sent.", "transport-none"));
}

/// <summary>
/// Writes a real RFC 5322 <c>.eml</c> rather than a text log, because an <c>.eml</c> opens
/// in Outlook and Apple Mail — and opening one is the only way to actually check that the
/// Arabic renders right.
/// <para>
/// The files do contain live download links, so the drop is server-owned, never web-served,
/// and swept at seven days.
/// </para>
/// </summary>
public sealed partial class FileDropEmailSender(
    IOptions<EmailOptions> options,
    EmailIdentity identity,
    ILogger<FileDropEmailSender> log,
    TimeProvider clock) : IEmailSender
{
    public async Task<EmailSendResult> SendAsync(OutgoingEmail message, CancellationToken ct = default)
    {
        try
        {
            var directory = Path.GetFullPath(options.Value.FileDrop.Path);
            Directory.CreateDirectory(directory);

            var name = string.Create(CultureInfo.InvariantCulture,
                $"{clock.GetUtcNow():yyyyMMdd-HHmmss}-{Sanitise(message.MessageId)}.eml");
            var path = Path.Combine(directory, name);

            var mime = MimeFactory.Build(message, await identity.ResolveAsync(ct));
            await using (var stream = File.Create(path))
            {
                await mime.WriteToAsync(stream, ct);
            }

            LogDropped(log, path);
            return new EmailSendResult(EmailSendOutcome.Sent, path, "file-drop");
        }
        catch (IOException ex)
        {
            return new EmailSendResult(EmailSendOutcome.TransientFailure, ex.Message, "file-drop-io");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new EmailSendResult(EmailSendOutcome.TransientFailure, ex.Message, "file-drop-denied");
        }
    }

    private static string Sanitise(string value) =>
        new([.. value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);

    [LoggerMessage(EventId = 5130, Level = LogLevel.Information, Message = "Wrote {Path}.")]
    private static partial void LogDropped(ILogger logger, string path);
}

/// <summary>
/// SMTP, through MailKit. A failure here is classified on the enhanced status code the
/// server returned, never on the command that carried it.
/// </summary>
public sealed partial class SmtpEmailSender(
    IOptions<EmailOptions> options,
    EmailIdentity identity,
    ILogger<SmtpEmailSender> log) : IEmailSender
{
    public async Task<EmailSendResult> SendAsync(OutgoingEmail message, CancellationToken ct = default)
    {
        var smtp = options.Value.Smtp;
        using var client = new SmtpClient { Timeout = smtp.TimeoutSeconds * 1000 };

        try
        {
            var from = await identity.ResolveAsync(ct);
            var mime = MimeFactory.Build(message, from);

            // The envelope sender carries the VERP reference; the visible From: stays the
            // store address, so a bounce names the message without the customer seeing it.
            if (message.ReturnPath is { Length: > 0 } returnPath)
                mime.Headers[HeaderId.Sender] = returnPath;

            await client.ConnectAsync(
                smtp.Host, smtp.Port,
                smtp.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto, ct);

            if (!string.IsNullOrWhiteSpace(smtp.User))
                await client.AuthenticateAsync(smtp.User, smtp.Password, ct);

            var envelopeSender = message.ReturnPath is { Length: > 0 } path
                ? MailboxAddress.Parse(path)
                : new MailboxAddress(from.Name, from.Address);

            var response = await client.SendAsync(mime, envelopeSender, [new MailboxAddress(message.ToName, message.ToEmail)], ct);
            await client.DisconnectAsync(quit: true, ct);

            return new EmailSendResult(EmailSendOutcome.Sent, response, "250");
        }
        catch (SmtpCommandException ex)
        {
            var classification = SmtpFailureClassifier.Classify((int)ex.StatusCode, ex.Message);
            if (classification.Kind == FailureKind.SenderPolicy) LogSenderPolicy(log, ex.Message);
            return new EmailSendResult(classification.Outcome, ex.Message, classification.Code);
        }
        catch (AuthenticationException ex)
        {
            // Transient, but loud: this is a misconfiguration and every email in the store
            // is about to fail in exactly the same way.
            LogAuthenticationFailed(log, ex.Message);
            return new EmailSendResult(EmailSendOutcome.TransientFailure, ex.Message, "535");
        }
        catch (SmtpProtocolException ex)
        {
            return new EmailSendResult(EmailSendOutcome.TransientFailure, ex.Message, "protocol");
        }
        catch (SslHandshakeException ex)
        {
            return new EmailSendResult(EmailSendOutcome.TransientFailure, ex.Message, "tls");
        }
        catch (IOException ex)
        {
            return new EmailSendResult(EmailSendOutcome.TransientFailure, ex.Message, "io");
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            return new EmailSendResult(EmailSendOutcome.TransientFailure, ex.Message, "socket");
        }
    }

    [LoggerMessage(EventId = 5140, Level = LogLevel.Error,
        Message = "SMTP rejected our sender policy (5.7.x): {Diagnostic}. " +
                  "SPF/DKIM/DMARC on the sending domain, not the recipient, is the thing to fix.")]
    private static partial void LogSenderPolicy(ILogger logger, string diagnostic);

    [LoggerMessage(EventId = 5141, Level = LogLevel.Error,
        Message = "SMTP authentication failed: {Diagnostic}. Every queued email will fail until this is fixed.")]
    private static partial void LogAuthenticationFailed(ILogger logger, string diagnostic);
}
