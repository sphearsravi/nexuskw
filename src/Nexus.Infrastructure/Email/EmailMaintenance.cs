using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexus.Application.Common;
using Nexus.Domain;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Email;

/// <summary>
/// Writes the default <c>email.*</c> settings once.
/// <para>
/// Idempotent in the manner <c>CatalogueSeeder</c> established: a key that already exists
/// is left alone, so a redeploy never overwrites an operator's wording. Not
/// <c>HasData</c> — that rewrites seed rows on the next migration that touches them, which
/// would silently destroy an edited template.
/// </para>
/// </summary>
public sealed partial class EmailTemplateSeeder(
    NexusDbContext db, ILogger<EmailTemplateSeeder> log, TimeProvider clock)
{
    public async Task<int> SeedAsync(CancellationToken ct = default)
    {
        var existing = await db.Settings
            .Where(s => s.Key.StartsWith("email."))
            .Select(s => s.Key)
            .ToListAsync(ct);

        var have = existing.ToHashSet(StringComparer.Ordinal);
        var now = clock.GetUtcNow();
        var added = 0;

        foreach (var (key, value) in EmailTemplates.Defaults)
        {
            if (have.Contains(key)) continue;

            db.Settings.Add(new Setting
            {
                Key = key,
                Value = value,
                // No SMTP credential and no gateway key is a Setting in these phases.
                IsSecret = false,
                UpdatedAt = now,
                UpdatedBy = "seed"
            });
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
            LogSeeded(log, added);
        }

        return added;
    }

    [LoggerMessage(EventId = 5180, Level = LogLevel.Information,
        Message = "Seeded {Count} email setting(s); existing keys were left alone.")]
    private static partial void LogSeeded(ILogger logger, int count);
}

/// <summary>What the nightly sweep found, for the operator's digest.</summary>
public sealed record OutboxDigest(
    int QueuedOverAnHour, int FailedLast24h, int AbandonedLast24h, int StuckSending, int DropFilesDeleted);

/// <summary>
/// The nightly pass. Reports the same four sets the health check watches and clears the
/// development mail drop.
/// <para>
/// Not a second timer: it is meant to be called from the existing reconciliation service,
/// under the same job lease. Two background loops racing one app pool is how the one that
/// matters gets starved.
/// </para>
/// </summary>
public sealed partial class EmailMaintenance(
    NexusDbContext db,
    IOptions<EmailOptions> options,
    ILogger<EmailMaintenance> log,
    TimeProvider clock)
{
    /// <summary>The <c>.eml</c> files do contain live download links, so they do not accumulate.</summary>
    public static readonly TimeSpan DropRetention = TimeSpan.FromDays(7);

    public async Task<OutboxDigest> SweepAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var hourAgo = now.AddHours(-1);
        var dayAgo = now.AddDays(-1);

        var queued = await db.OutboxEmails.CountAsync(
            e => e.Status == EmailStatus.Queued && e.CreatedAt < hourAgo, ct);
        var failed = await db.OutboxEmails.CountAsync(
            e => e.Status == EmailStatus.Failed && e.CreatedAt >= dayAgo, ct);
        var abandoned = await db.OutboxEmails.CountAsync(
            e => e.Status == EmailStatus.Abandoned && e.CreatedAt >= dayAgo, ct);
        var stuck = await db.OutboxEmails.CountAsync(
            e => e.Status == EmailStatus.Sending && e.NextAttemptAt != null && e.NextAttemptAt < now, ct);

        if (queued > 0) LogStaleQueue(log, queued);

        var digest = new OutboxDigest(queued, failed, abandoned, stuck, SweepDrop(now));
        LogDigest(log, digest.QueuedOverAnHour, digest.FailedLast24h, digest.AbandonedLast24h,
            digest.StuckSending, digest.DropFilesDeleted);

        return digest;
    }

    private int SweepDrop(DateTimeOffset now)
    {
        var path = options.Value.FileDrop.Path;
        if (!Directory.Exists(path)) return 0;

        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*.eml"))
        {
            try
            {
                if (now - File.GetLastWriteTimeUtc(file) < DropRetention) continue;
                File.Delete(file);
                deleted++;
            }
            catch (IOException)
            {
                // A file somebody has open in a mail client is not worth failing a sweep for.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return deleted;
    }

    [LoggerMessage(EventId = 5190, Level = LogLevel.Error,
        Message = "{Count} email(s) have been queued for over an hour. The dispatcher is probably not running " +
                  "— check that the app pool is set to Always Running.")]
    private static partial void LogStaleQueue(ILogger logger, int count);

    [LoggerMessage(EventId = 5191, Level = LogLevel.Information,
        Message = "Outbox digest: {Queued} stale queued, {Failed} failed, {Abandoned} abandoned, " +
                  "{Stuck} stuck sending, {Deleted} drop file(s) deleted.")]
    private static partial void LogDigest(
        ILogger logger, int queued, int failed, int abandoned, int stuck, int deleted);
}
