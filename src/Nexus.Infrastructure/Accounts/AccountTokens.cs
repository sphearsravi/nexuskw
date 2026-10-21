using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Nexus.Infrastructure.Accounts;

/// <summary>
/// Moves an Identity token through a URL without it being mangled.
/// <para>
/// A raw Identity token is base64 with <c>+</c>, <c>/</c> and <c>=</c> in it. Mail
/// clients, link rewriters and the <c>+</c>-means-space rule between them corrupt it
/// often enough that base64url is what the framework's own templates use, so it is what
/// this uses too.
/// </para>
/// </summary>
public static class AccountTokens
{
    public static string Encode(string token) =>
        WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

    /// <summary>Null when the value is not something this produced. Never throws at the caller.</summary>
    public static string? Decode(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return null;

        try { return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(encoded.Trim())); }
        catch (FormatException) { return null; }
    }
}
