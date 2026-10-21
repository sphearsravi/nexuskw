using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Nexus.Domain;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Email;

/// <summary>
/// What the outbox looks like right now.
/// <para>
/// It counts four things, not one. Measuring only the age of the oldest queued row leaves
/// every undelivered-but-terminal state invisible: Failed, Abandoned, and rows stuck in
/// Sending past a second lease were counted by nothing, and there is no admin screen until
/// Phase 7. A queue that is empty because everything in it failed is not healthy.
/// </para>
/// </summary>
public sealed class OutboxHealthCheck(
    NexusDbContext db,
    SuppressionCircuitBreaker breaker,
    IOptions<EmailOptions> options,
    IOptions<SiteOptions> site,
    TimeProvider clock) : IHealthCheck
{
    public static readonly TimeSpan DegradedAfter = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan UnhealthyAfter = TimeSpan.FromMinutes(15);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var reasons = new List<string>();
        var worst = HealthStatus.Healthy;

        void Note(HealthStatus status, string reason)
        {
            reasons.Add(reason);
            if (status < worst) worst = status;
        }

        var oldestQueued = await db.OutboxEmails
            .Where(e => e.Status == EmailStatus.Queued)
            .OrderBy(e => e.CreatedAt)
            .Select(e => (DateTimeOffset?)e.CreatedAt)
            .FirstOrDefaultAsync(ct);

        var queuedAge = oldestQueued is null ? TimeSpan.Zero : now - oldestQueued.Value;
        if (queuedAge >= UnhealthyAfter)
            Note(HealthStatus.Unhealthy, $"oldest queued email is {(int)queuedAge.TotalMinutes} minutes old");
        else if (queuedAge >= DegradedAfter)
            Note(HealthStatus.Degraded, $"oldest queued email is {(int)queuedAge.TotalMinutes} minutes old");

        var dayAgo = now.AddDays(-1);
        var terminal = await db.OutboxEmails.CountAsync(
            e => (e.Status == EmailStatus.Failed || e.Status == EmailStatus.Abandoned) && e.CreatedAt >= dayAgo, ct);
        if (terminal >= 5)
            Note(HealthStatus.Unhealthy, $"{terminal} emails failed or were abandoned in the last 24 hours");
        else if (terminal >= 1)
            Note(HealthStatus.Degraded, $"{terminal} email(s) failed or were abandoned in the last 24 hours");

        var stuck = await db.OutboxEmails.CountAsync(
            e => e.Status == EmailStatus.Sending && e.NextAttemptAt != null && e.NextAttemptAt < now, ct);
        if (stuck > 0)
            Note(HealthStatus.Unhealthy, $"{stuck} email(s) are stuck mid-send with an expired lease");

        foreach (var problem in options.Value.Problems())
            Note(HealthStatus.Degraded, problem);

        /* A base URL that is not absolute ships a relative path into a mail client, which
         * is a dead link that looks entirely correct in every test that renders the body
         * to a string. It has to be caught here or it is caught by a customer. */
        foreach (var problem in site.Value.Problems())
            Note(HealthStatus.Degraded, problem);

        if (options.Value.EffectiveTransport() == EmailTransport.None)
            Note(HealthStatus.Degraded, "Email:Transport resolves to None; nothing is being sent");

        if (breaker.IsOpen)
            Note(HealthStatus.Unhealthy,
                $"suppression circuit breaker is open after {breaker.ConsecutivePermanentFailures} " +
                "consecutive permanent failures; the sending configuration is the likely cause");

        var data = new Dictionary<string, object>
        {
            ["oldestQueuedMinutes"] = (int)queuedAge.TotalMinutes,
            ["failedOrAbandoned24h"] = terminal,
            ["stuckSending"] = stuck,
            ["transport"] = options.Value.EffectiveTransport().ToString()
        };

        return new HealthCheckResult(
            worst,
            reasons.Count == 0 ? "outbox is clear" : string.Join("; ", reasons),
            data: data);
    }
}
