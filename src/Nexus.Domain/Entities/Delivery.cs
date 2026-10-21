namespace Nexus.Domain.Entities;

/// <summary>
/// One buyer's right to download one book from one order.
/// <para>
/// <see cref="TokenHash"/> is the only lookup key — the plaintext token never appears in
/// a query. <see cref="TokenProtected"/> holds the same token encrypted with the
/// DataProtection key ring, so the email dispatcher can still build a working link at
/// send time without a database copy being download access: the keys live on the
/// filesystem in <c>App_Data/dpkeys</c>, which is not in the database backup set.
/// </para>
/// </summary>
public class DownloadGrant
{
    public int Id { get; set; }

    public int OrderId { get; set; }
    public Order? Order { get; set; }

    public int BookId { get; set; }
    public Book? Book { get; set; }

    /// <summary>1 for the original grant, incremented on each re-issue.</summary>
    public int Sequence { get; set; } = 1;

    /// <summary>SHA-256 of the plaintext token, lowercase hex. The only thing looked up.</summary>
    public required string TokenHash { get; set; }

    /// <summary>The token, encrypted at rest. Never logged, never returned by an API.</summary>
    public byte[]? TokenProtected { get; set; }

    public int MaxDownloads { get; set; }
    public int UsedCount { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public GrantState State { get; set; } = GrantState.Active;
    public RevocationReason RevocationReason { get; set; } = RevocationReason.None;
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Set when the grant has been used from more distinct addresses than the threshold allows.</summary>
    public DateTimeOffset? FlaggedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<DownloadLog> Logs { get; set; } = [];

    /// <summary>
    /// Why this grant cannot serve a file right now, or null when it can. State, expiry
    /// and count are all checked — the existence of a valid token is not enough.
    /// </summary>
    public string? ServeBlocker(DateTimeOffset now)
    {
        if (State == GrantState.Revoked) return "revoked";
        if (State == GrantState.Exhausted || UsedCount >= MaxDownloads) return "exhausted";
        if (State == GrantState.Expired || now >= ExpiresAt) return "expired";
        return null;
    }
}

/// <summary>Every download attempt, served or refused. This is what the admin screen shows.</summary>
public class DownloadLog
{
    public long Id { get; set; }

    public int GrantId { get; set; }
    public DownloadGrant? Grant { get; set; }

    public DateTimeOffset AtUtc { get; set; }

    /// <summary>Truncated address — enough to spot sharing, less than is needed to track a person.</summary>
    public string? IpPrefix { get; set; }

    public string? UserAgent { get; set; }
    public DownloadOutcome Outcome { get; set; }
    public long? BytesSent { get; set; }
}
