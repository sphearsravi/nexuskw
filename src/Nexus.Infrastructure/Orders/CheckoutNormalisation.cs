using System.Net.Mail;

namespace Nexus.Infrastructure.Orders;

/// <summary>Turns what the browser typed into what the database stores.</summary>
public static class CheckoutNormalisation
{
    public const string DefaultLanguage = "en";

    /// <summary>
    /// <c>"AR"</c>, <c>"ar-KW"</c> and <c>"ar "</c> all resolve to <c>ar</c>; anything
    /// else falls back to English. Never an error: the value comes from our own
    /// storefront, so an unexpected one is our bug, and failing a purchase over it is
    /// the wrong response. <paramref name="recognised"/> lets the caller log it.
    /// </summary>
    public static string Language(string? value, out bool recognised)
    {
        var trimmed = (value ?? string.Empty).Trim().ToLowerInvariant();
        var prefix = trimmed.Length >= 2 ? trimmed[..2] : trimmed;

        recognised = prefix is "en" or "ar";
        return prefix == "ar" ? "ar" : DefaultLanguage;
    }

    /// <summary>
    /// Deliberately loose: a parseable address with a dot in the host, and a length
    /// bound. Clever regexes reject valid addresses, and the address is proved in
    /// Stage D by an email that either arrives or does not.
    /// </summary>
    public static bool IsPlausibleEmail(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength) return false;
        if (!MailAddress.TryCreate(trimmed, out var address)) return false;

        // TryCreate accepts "a@b"; a deliverable address has a dotted host.
        return address.Host.Contains('.', StringComparison.Ordinal)
            && !address.Host.StartsWith('.')
            && !address.Host.EndsWith('.');
    }
}
