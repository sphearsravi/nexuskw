using System.Text.RegularExpressions;
using Nexus.Application.Email;

namespace Nexus.Infrastructure.Email;

public enum FailureKind
{
    /// <summary>Worth retrying: greylisting, a timeout, DNS, TLS, a 4.x.x.</summary>
    Transient = 0,
    /// <summary>The mailbox does not exist. The address is a suppression candidate.</summary>
    RecipientHardBounce = 1,
    /// <summary>
    /// SPF, DKIM or DMARC refused <em>us</em>. Permanent for this attempt, and the address
    /// is innocent.
    /// </summary>
    SenderPolicy = 2,
    /// <summary>Some other permanent rejection — our message, not their mailbox.</summary>
    MessageRejected = 3
}

public sealed record SmtpClassification(EmailSendOutcome Outcome, FailureKind Kind, string Code)
{
    public bool IsSuppressionCandidate => Kind == FailureKind.RecipientHardBounce;
}

/// <summary>
/// Decides what a failed send means.
/// <para>
/// <b>It classifies on the enhanced status code, never on the SMTP command.</b> 5.7.1,
/// 5.7.23 and 5.7.26 are returned at <c>RCPT TO</c> exactly as 5.1.1 is, and they mean the
/// receiving domain rejected our sender policy — not that the mailbox is dead. Treating
/// them as hard bounces on the first day of real sending, with SPF and DKIM not yet
/// propagated, would permanently and silently suppress every customer who bought that day,
/// with the health check green.
/// </para>
/// </summary>
public static partial class SmtpFailureClassifier
{
    public static SmtpClassification Network(string reason) =>
        new(EmailSendOutcome.TransientFailure, FailureKind.Transient, reason);

    public static SmtpClassification Classify(int? smtpStatusCode, string? diagnostic)
    {
        var enhanced = EnhancedCode(diagnostic);

        // 535 is an authentication failure: transient for this message, and a configuration
        // emergency, because every email in the store is about to fail the same way.
        if (smtpStatusCode == 535 || enhanced.StartsWith("5.7.8", StringComparison.Ordinal))
            return new(EmailSendOutcome.TransientFailure, FailureKind.Transient, enhanced.Length > 0 ? enhanced : "535");

        if (enhanced.Length > 0)
        {
            if (enhanced is "5.1.1" or "5.1.2" or "5.1.10")
                return new(EmailSendOutcome.PermanentFailure, FailureKind.RecipientHardBounce, enhanced);

            if (enhanced.StartsWith("5.7.", StringComparison.Ordinal))
                return new(EmailSendOutcome.PermanentFailure, FailureKind.SenderPolicy, enhanced);

            if (enhanced.StartsWith('5'))
                return new(EmailSendOutcome.PermanentFailure, FailureKind.MessageRejected, enhanced);

            if (enhanced.StartsWith('4'))
                return new(EmailSendOutcome.TransientFailure, FailureKind.Transient, enhanced);
        }

        /* No enhanced code at all. A bare 5xx is still permanent, but we will not suppress
         * an address on it: without the code we cannot tell "no such user" from "your SPF
         * is wrong", and only one of those is the customer's fault. */
        return smtpStatusCode switch
        {
            >= 500 => new(EmailSendOutcome.PermanentFailure, FailureKind.MessageRejected,
                smtpStatusCode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            >= 400 => new(EmailSendOutcome.TransientFailure, FailureKind.Transient,
                smtpStatusCode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            _ => new(EmailSendOutcome.TransientFailure, FailureKind.Transient, "unclassified")
        };
    }

    /// <summary>The first <c>x.y.z</c> in the server's diagnostic, or an empty string.</summary>
    public static string EnhancedCode(string? diagnostic) =>
        diagnostic is null ? "" : EnhancedPattern().Match(diagnostic) switch
        {
            { Success: true } m => m.Value,
            _ => ""
        };

    [GeneratedRegex(@"\b[245]\.\d{1,3}\.\d{1,3}\b")]
    private static partial Regex EnhancedPattern();
}
