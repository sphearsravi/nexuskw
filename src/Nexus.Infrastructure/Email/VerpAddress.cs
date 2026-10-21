namespace Nexus.Infrastructure.Email;

/// <summary>
/// The envelope sender and the Message-Id.
/// <para>
/// A variable envelope return path — <c>bounces+{publicRef}@{bounceDomain}</c> while the
/// visible <c>From:</c> stays the store address — means every asynchronous bounce that
/// arrives names the exact message that caused it, from the first email, whether or not
/// anything is yet reading the mailbox. The public reference rather than the row id, so the
/// address does not disclose the store's sending volume to everyone who receives one.
/// </para>
/// </summary>
public static class VerpAddress
{
    /// <summary>
    /// Stable across every attempt, so Gmail and Exchange Online collapse the duplicate a
    /// recovered lease can produce.
    /// </summary>
    public static string MessageId(string publicRef, string? bounceDomain) =>
        $"{publicRef}@{DomainOrFallback(bounceDomain)}";

    /// <summary>
    /// The envelope sender, or null when the bounce domain cannot be used — in which case
    /// the caller falls back to the visible <c>From:</c> and the reason is logged.
    /// </summary>
    public static (string? ReturnPath, string? Problem) ReturnPath(
        string fromAddress, string? bounceDomain, string publicRef)
    {
        if (string.IsNullOrWhiteSpace(bounceDomain))
            return (null, "Email:BounceDomain is not set; the envelope sender falls back to the From: address.");

        var bounce = bounceDomain.Trim().TrimStart('@').ToLowerInvariant();
        var from = DomainOf(fromAddress);

        if (from is null)
            return (null, $"The From: address '{fromAddress}' has no domain; VERP is not used.");

        /* DMARC aligns SPF on MAIL FROM. A bounce domain that is not the From:
         * organisational domain or a subdomain of it quarantines every message we send,
         * which is the single most expensive misconfiguration available here. */
        if (bounce != from && !bounce.EndsWith('.' + from))
        {
            return (null,
                $"Email:BounceDomain '{bounce}' does not align with the From: domain '{from}'; " +
                "VERP is disabled so DMARC does not quarantine every message.");
        }

        return ($"bounces+{publicRef}@{bounce}", null);
    }

    private static string DomainOrFallback(string? bounceDomain) =>
        string.IsNullOrWhiteSpace(bounceDomain) ? "nexus.invalid" : bounceDomain.Trim().TrimStart('@').ToLowerInvariant();

    private static string? DomainOf(string address)
    {
        var at = address.LastIndexOf('@');
        return at < 0 || at == address.Length - 1 ? null : address[(at + 1)..].Trim().ToLowerInvariant();
    }
}
