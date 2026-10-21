using System.Security.Cryptography;
using System.Text;

namespace Nexus.Infrastructure.Delivery;

/// <summary>
/// The download token: 256 bits from the CSPRNG, Base64Url-encoded to 43 characters.
/// <para>
/// The repository is public, so the format is known and entropy is the only thing
/// protecting a link. A GUID would give 122 bits and advertise its own structure; this
/// gives 256 and reads as a secret. Only <see cref="Hash"/> is ever persisted or
/// queried — the plaintext exists in memory at issue and, encrypted, in
/// <c>DownloadGrant.TokenProtected</c>.
/// </para>
/// </summary>
public static class DownloadToken
{
    public const int Length = 43;

    public static string New() =>
        Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Lowercase hex SHA-256. The only lookup key.</summary>
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token))).ToLowerInvariant();

    /// <summary>
    /// Shape check before any database call, so an enumeration attempt costs us a string
    /// comparison rather than an index seek.
    /// </summary>
    public static bool LooksWellFormed(string? token)
    {
        if (token is not { Length: Length }) return false;
        foreach (var c in token)
        {
            var ok = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';
            if (!ok) return false;
        }
        return true;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
