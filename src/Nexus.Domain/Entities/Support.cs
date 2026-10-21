namespace Nexus.Domain.Entities;

/// <summary>
/// Operator-editable configuration. Seeded by an idempotent runtime seeder, never by
/// <c>HasData</c> — that rewrites seed rows on the next migration that touches them, which
/// would silently destroy an operator's edited email copy.
/// <para>
/// No SMTP credential and no gateway key is a Setting in these phases; those live in
/// server environment configuration.
/// </para>
/// </summary>
public class Setting
{
    /// <summary>Dotted, lowercase-first: <c>delivery.linkExpiryDays</c>.</summary>
    public required string Key { get; set; }

    public required string Value { get; set; }

    /// <summary>Masked in the admin and never returned by any public endpoint.</summary>
    public bool IsSecret { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// A queued email. It stores no rendered body: the dispatcher re-materialises the model
/// from <see cref="OrderId"/>, <see cref="Kind"/> and <see cref="Language"/> at send time
/// and re-validates the grants. That keeps live download tokens out of the database and
/// out of every backup, and makes a retry eight days later either correct or honestly
/// refused rather than a dead link.
/// </summary>
public class OutboxEmail
{
    public long Id { get; set; }

    public int? OrderId { get; set; }
    public Order? Order { get; set; }

    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public EmailKind Kind { get; set; }
    public required string ToEmail { get; set; }
    public string Language { get; set; } = "en";

    /// <summary>Stable across retries, so one paid order sends exactly one email.</summary>
    public required string IdempotencyKey { get; set; }

    /// <summary>Opaque public reference used in the Message-Id and for bounce matching.</summary>
    public required string PublicRef { get; set; }

    /// <summary><c>{PublicRef}@{bounceDomain}</c>, stable across retries.</summary>
    public string? MessageId { get; set; }

    /// <summary>Written at send, for the admin list. Not used to render.</summary>
    public string? Subject { get; set; }

    public EmailStatus Status { get; set; } = EmailStatus.Queued;
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>An address we must stop writing to, and why.</summary>
public class EmailSuppression
{
    public int Id { get; set; }

    public required string NormalisedEmail { get; set; }
    public SuppressionReason Reason { get; set; }

    /// <summary>The enhanced status code that caused it, when there was one.</summary>
    public string? LastClassification { get; set; }

    public int SoftBounceCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Who changed what. Matters most for price changes and refunds, where the question is
/// always asked weeks later.
/// </summary>
public class AuditLog
{
    public long Id { get; set; }

    public string? ActorId { get; set; }
    public string? ActorEmail { get; set; }

    public required string Entity { get; set; }
    public required string EntityId { get; set; }
    public required string Action { get; set; }

    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }

    public DateTimeOffset At { get; set; }
    public string? IpAddress { get; set; }
}
