namespace Nexus.Domain.Entities;

/// <summary>
/// A buyer. A guest checkout creates one of these with no
/// <see cref="IdentityUserId"/>; registering later with the same confirmed email
/// claims it, bringing the prior orders into the account's library.
/// <para>
/// <b>This row is a mutable convenience.</b> No order, invoice, watermark or email
/// reads <see cref="Name"/> or <see cref="Email"/> — they read the snapshot on the
/// order, because an unauthenticated guest checkout can write here.
/// </para>
/// </summary>
public class Customer
{
    public int Id { get; set; }

    public required string Email { get; set; }

    /// <summary>Trimmed and upper-cased, for the unique index and for lookup.</summary>
    public required string NormalisedEmail { get; set; }

    public required string Name { get; set; }

    /// <summary>ISO 3166-1 alpha-2. Always "KW" in v1.</summary>
    public string Country { get; set; } = "KW";

    public string PreferredLanguage { get; set; } = "en";

    // No PreferredCurrency: v1 is KWD only.

    /// <summary>Null while this is a guest. Set when an account claims the email.</summary>
    public string? IdentityUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<Order> Orders { get; set; } = [];

    public static string Normalise(string email) => email.Trim().ToUpperInvariant();
}
