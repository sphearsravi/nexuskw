using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexus.Application.Email;
using Nexus.Domain;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Email;

/// <summary>
/// Stops the store writing to an address that cannot receive.
/// <para>
/// Suppression is deliberately hard to earn. The commonest cause of a rejection is us — an
/// unpropagated SPF record, a new sending domain — and a permanent block over a temporary
/// condition is how a store loses a repeat buyer who never finds out why.
/// </para>
/// </summary>
public sealed partial class EmailSuppressionList(
    NexusDbContext db,
    SuppressionCircuitBreaker breaker,
    ILogger<EmailSuppressionList> log,
    TimeProvider clock) : IEmailSuppressionList
{
    /// <summary>Trimmed and lowercased. The one form every comparison uses.</summary>
    public static string Normalise(string email) => email.Trim().ToLowerInvariant();

    /// <summary>The marker a hard bounce leaves on the outbox row, and the ledger the two-day rule reads.</summary>
    public const string HardBounceMarker = "hard-bounce";

    public Task<bool> IsSuppressedAsync(string email, CancellationToken ct = default)
    {
        var normalised = Normalise(email);
        return db.EmailSuppressions.AnyAsync(s => s.NormalisedEmail == normalised, ct);
    }

    public Task<EmailSuppression?> FindAsync(string email, CancellationToken ct = default)
    {
        var normalised = Normalise(email);
        return db.EmailSuppressions.FirstOrDefaultAsync(s => s.NormalisedEmail == normalised, ct);
    }

    public async Task SuppressAsync(
        string email, SuppressionReason reason, string? classification, CancellationToken ct = default)
    {
        var normalised = Normalise(email);
        var now = clock.GetUtcNow();
        var existing = await db.EmailSuppressions.FirstOrDefaultAsync(s => s.NormalisedEmail == normalised, ct);

        if (existing is null)
        {
            db.EmailSuppressions.Add(new EmailSuppression
            {
                NormalisedEmail = normalised,
                Reason = reason,
                LastClassification = classification,
                SoftBounceCount = reason == SuppressionReason.RepeatedSoftBounce ? 1 : 0,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            existing.Reason = reason;
            existing.LastClassification = classification ?? existing.LastClassification;
            existing.UpdatedAt = now;
            if (reason == SuppressionReason.RepeatedSoftBounce) existing.SoftBounceCount++;
        }

        await db.SaveChangesAsync(ct);
        LogSuppressed(log, normalised, reason, classification ?? "none");
    }

    /// <summary>Lifts a block. The row is removed and the removal audited by the caller.</summary>
    public async Task<bool> ClearAsync(string email, CancellationToken ct = default)
    {
        var normalised = Normalise(email);
        var existing = await db.EmailSuppressions.FirstOrDefaultAsync(s => s.NormalisedEmail == normalised, ct);
        if (existing is null) return false;

        db.EmailSuppressions.Remove(existing);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// A hard bounce arrived. Suppresses only when the evidence is good enough.
    /// <para>
    /// Two conditions, both of which exist because the expensive failure is suppressing a
    /// live customer base over our own misconfiguration: the address must have hard-bounced
    /// on <b>two different Kuwait calendar days</b>, and the circuit breaker must not be
    /// open. Ten consecutive permanent failures across distinct recipients means the common
    /// factor is us, so suppression stops entirely and the health check goes red.
    /// </para>
    /// </summary>
    public async Task<bool> ConsiderHardBounceAsync(
        string email, string classification, CancellationToken ct = default)
    {
        if (breaker.IsOpen)
        {
            LogBreakerHeld(log, Normalise(email), breaker.ConsecutivePermanentFailures);
            return false;
        }

        var normalised = Normalise(email);

        var failureDates = await db.OutboxEmails
            .Where(e => e.ToEmail.ToLower() == normalised
                        && e.Status == EmailStatus.Failed
                        && e.LastError != null
                        && e.LastError.StartsWith(HardBounceMarker))
            .Select(e => e.CreatedAt)
            .ToListAsync(ct);

        var distinctDays = failureDates
            .Select(d => KuwaitDates.ToKuwait(d).Date)
            .Distinct()
            .Count();

        if (distinctDays < 2)
        {
            LogAwaitingSecondDay(log, normalised, distinctDays);
            return false;
        }

        await SuppressAsync(email, SuppressionReason.HardBounce, classification, ct);
        return true;
    }

    [LoggerMessage(EventId = 5120, Level = LogLevel.Warning,
        Message = "Suppressed {Email} ({Reason}, {Classification}).")]
    private static partial void LogSuppressed(
        ILogger logger, string email, SuppressionReason reason, string classification);

    [LoggerMessage(EventId = 5121, Level = LogLevel.Information,
        Message = "Hard bounce for {Email} on {DistinctDays} distinct day(s); not suppressing yet.")]
    private static partial void LogAwaitingSecondDay(ILogger logger, string email, int distinctDays);

    [LoggerMessage(EventId = 5122, Level = LogLevel.Error,
        Message = "Suppression circuit breaker is open after {Failures} consecutive permanent failures; " +
                  "{Email} was NOT suppressed. The common factor is almost certainly our sending configuration.")]
    private static partial void LogBreakerHeld(ILogger logger, string email, int failures);
}

/// <summary>
/// Stops the suppression list eating the customer base when the fault is ours.
/// <para>
/// Singleton, in memory, deliberately: it is a reading of what is happening right now, and
/// a process recycle resetting it is the correct behaviour — the next ten failures will
/// trip it again within a minute.
/// </para>
/// </summary>
public sealed class SuppressionCircuitBreaker
{
    public const int Threshold = 10;

    private readonly object _gate = new();
    private readonly HashSet<string> _recipients = new(StringComparer.Ordinal);
    private int _consecutive;

    public bool IsOpen
    {
        get { lock (_gate) return _recipients.Count >= Threshold; }
    }

    public int ConsecutivePermanentFailures
    {
        get { lock (_gate) return _consecutive; }
    }

    public void RecordPermanentFailure(string normalisedEmail)
    {
        lock (_gate)
        {
            _consecutive++;
            _recipients.Add(normalisedEmail);
        }
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutive = 0;
            _recipients.Clear();
        }
    }
}
