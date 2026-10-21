using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexus.Application.Email;
using Nexus.Domain;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Email;

/// <summary>
/// One pass of the outbox: recover abandoned leases, claim a batch, send it.
/// <para>
/// Separate from the hosted service so it can be driven directly by a test. Sends are
/// serial, not parallel: order volume is tens a day, providers rate-limit connections, and
/// the app pool is 512 MB shared with five other sites. There is nothing to gain.
/// </para>
/// </summary>
public sealed partial class OutboxPump(
    NexusDbContext db,
    IEmailTemplateRenderer renderer,
    IEmailSender sender,
    EmailSuppressionList suppressions,
    EmailIdentity identity,
    EmailRetryPolicy retry,
    SuppressionCircuitBreaker breaker,
    IOptions<EmailOptions> options,
    ILogger<OutboxPump> log,
    TimeProvider clock)
{
    /// <summary>
    /// The lease marker. There is no <c>LockedUntil</c> column in the one migration, so a
    /// claimed row carries its lease deadline in <see cref="OutboxEmail.NextAttemptAt"/>
    /// while it is <c>Sending</c> — the column means "do not touch this before" in both
    /// states, which is the same thing the dispatcher needs from it.
    /// </summary>
    public const string PossibleDuplicateMarker = "possible duplicate";

    private readonly Random _jitter = new();

    /// <summary>Rows processed. Zero means the queue was empty and the caller may idle.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        await RecoverExpiredLeasesAsync(ct);

        var claimed = await ClaimAsync(options.Value.BatchSize, ct);
        foreach (var id in claimed)
        {
            ct.ThrowIfCancellationRequested();
            await SendOneAsync(id, ct);
        }

        return claimed.Count;
    }

    /// <summary>
    /// A row left <c>Sending</c> past its lease is recovered once, marked as a possible
    /// duplicate so the operator can see it happened. A second expiry is a failure, not a
    /// third attempt: SMTP has no transaction, and a row that has twice died mid-send is
    /// telling us something a retry will not fix.
    /// </summary>
    public async Task RecoverExpiredLeasesAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();

        var dead = await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE OutboxEmails
               SET Status = {0}, LastError = 'lease expired twice', NextAttemptAt = NULL
             WHERE Status = {1} AND NextAttemptAt IS NOT NULL AND NextAttemptAt < {2}
               AND LastError IS NOT NULL AND LEFT(LastError, {3}) = {4}
            """,
            [(int)EmailStatus.Failed, (int)EmailStatus.Sending, now,
             PossibleDuplicateMarker.Length, PossibleDuplicateMarker], ct);

        var recovered = await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE OutboxEmails
               SET Status = {0}, LastError = {1}, NextAttemptAt = {2}
             WHERE Status = {3} AND NextAttemptAt IS NOT NULL AND NextAttemptAt < {2}
            """,
            [(int)EmailStatus.Queued,
             PossibleDuplicateMarker + ": the lease expired while the row was sending",
             now, (int)EmailStatus.Sending], ct);

        if (dead > 0 || recovered > 0) LogLeasesRecovered(log, recovered, dead);
    }

    /// <summary>
    /// Claims in one statement, ordered by <c>(NextAttemptAt, Id)</c>.
    /// <para>
    /// The order is the point. An unordered <c>UPDATE TOP (n)</c> delivers a post-outage
    /// backlog in whatever order the engine fancies, so the oldest queued row ages past the
    /// health check's fifteen minutes while newer mail sails out ahead of it.
    /// <c>READPAST</c> lets a second dispatcher skip the rows this one holds rather than
    /// block behind them.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<long>> ClaimAsync(int take, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var lease = now.AddMinutes(options.Value.LeaseMinutes);

        const string Sql =
            """
            UPDATE claimed
               SET Status = @status_sending,
                   AttemptCount = claimed.AttemptCount + 1,
                   NextAttemptAt = @lease
            OUTPUT inserted.Id
              FROM (SELECT TOP (@take) Id, Status, AttemptCount, NextAttemptAt
                      FROM OutboxEmails WITH (READPAST, ROWLOCK, UPDLOCK)
                     WHERE Status = @status_queued
                       AND NextAttemptAt IS NOT NULL
                       AND NextAttemptAt <= @now
                     ORDER BY NextAttemptAt, Id) AS claimed
            """;

        var ids = new List<long>();

        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        Add(command, "@status_sending", (int)EmailStatus.Sending);
        Add(command, "@status_queued", (int)EmailStatus.Queued);
        Add(command, "@take", Math.Clamp(take, 1, 100));
        Add(command, "@lease", lease);
        Add(command, "@now", now);

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetInt64(0));

        return ids;
    }

    public async Task SendOneAsync(long id, CancellationToken ct = default)
    {
        var row = await db.OutboxEmails.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (row is null) return;

        var now = clock.GetUtcNow();

        if (options.Value.EffectiveTransport() == EmailTransport.None)
        {
            await FinishAsync(row, EmailStatus.Suppressed, "sending disabled: Email:Transport resolves to None", ct);
            return;
        }

        // Re-checked at send, not only at enqueue: an address can bounce between the
        // payment and the dispatcher's next pass.
        if (await suppressions.IsSuppressedAsync(row.ToEmail, ct))
        {
            await FinishAsync(row, EmailStatus.Suppressed, "recipient suppressed before the message was sent", ct);
            return;
        }

        var (rendered, problem) = await renderer.RenderAsync(row, ct);
        if (rendered is null)
        {
            /* Refusing is the right answer, and it is a terminal one. "no_live_grants"
             * means every link on this order is dead; sending it anyway gives the customer
             * a link that fails, after which they distrust the next one too. The row is
             * Failed and visible, and the operator re-issues and resends. */
            await FinishAsync(row, EmailStatus.Failed, $"not rendered: {problem}", ct);
            LogNotRendered(log, row.PublicRef, problem ?? "unknown");
            return;
        }

        var from = await identity.ResolveAsync(ct);
        var (returnPath, verpProblem) = VerpAddress.ReturnPath(
            from.Address, options.Value.BounceDomain, row.PublicRef);
        if (verpProblem is not null) LogVerpDisabled(log, verpProblem);

        var message = new OutgoingEmail(
            row.ToEmail,
            await RecipientNameAsync(row, ct),
            rendered.Subject,
            rendered.Html,
            rendered.Text,
            row.MessageId ?? VerpAddress.MessageId(row.PublicRef, options.Value.BounceDomain),
            returnPath);

        var result = await sender.SendAsync(message, ct);
        row.Subject = Truncate(rendered.Subject, 256);

        switch (result.Outcome)
        {
            case EmailSendOutcome.Sent:
                breaker.RecordSuccess();
                row.Status = EmailStatus.Sent;
                row.SentAt = now;
                row.NextAttemptAt = null;
                row.LastError = null;
                await db.SaveChangesAsync(ct);
                LogSent(log, row.PublicRef, row.Kind, row.Language);
                return;

            case EmailSendOutcome.TransientFailure:
                var next = retry.NextAttemptAt(row.AttemptCount, _jitter);
                row.Status = next is null ? EmailStatus.Abandoned : EmailStatus.Queued;
                row.NextAttemptAt = next;
                row.LastError = Truncate($"transient ({result.Classification}): {result.Detail}", 1024);
                await db.SaveChangesAsync(ct);
                if (next is null) LogAbandoned(log, row.PublicRef, row.ToEmail);
                return;

            default:
                await HandlePermanentAsync(row, result, ct);
                return;
        }
    }

    private async Task HandlePermanentAsync(OutboxEmail row, EmailSendResult result, CancellationToken ct)
    {
        var classification = SmtpFailureClassifier.Classify(null, result.Detail);
        var code = result.Classification ?? classification.Code;
        var hardBounce = classification.Kind == FailureKind.RecipientHardBounce;

        row.Status = EmailStatus.Failed;
        row.NextAttemptAt = null;
        row.LastError = Truncate(
            hardBounce
                ? $"{EmailSuppressionList.HardBounceMarker} {code}: {result.Detail}"
                : $"permanent ({code}): {result.Detail}",
            1024);

        await db.SaveChangesAsync(ct);

        breaker.RecordPermanentFailure(EmailSuppressionList.Normalise(row.ToEmail));

        if (hardBounce)
            await suppressions.ConsiderHardBounceAsync(row.ToEmail, code, ct);
        else
            LogPermanent(log, row.PublicRef, code, result.Detail ?? "");
    }

    /// <summary>
    /// The buyer's name as the order snapshotted it, never the live
    /// <see cref="Customer"/> row — which an unauthenticated guest checkout can rewrite.
    /// </summary>
    private async Task<string> RecipientNameAsync(OutboxEmail row, CancellationToken ct)
    {
        if (row.OrderId is { } orderId)
        {
            var name = await db.Orders
                .Where(o => o.Id == orderId)
                .Select(o => o.BuyerNameSnapshot)
                .FirstOrDefaultAsync(ct);

            if (!string.IsNullOrWhiteSpace(name)) return name;
        }

        if (row.CustomerId is { } customerId)
        {
            var name = await db.Customers
                .Where(c => c.Id == customerId)
                .Select(c => c.Name)
                .FirstOrDefaultAsync(ct);

            if (!string.IsNullOrWhiteSpace(name)) return name;
        }

        var at = row.ToEmail.IndexOf('@');
        return at > 0 ? row.ToEmail[..at] : row.ToEmail;
    }

    private async Task FinishAsync(OutboxEmail row, EmailStatus status, string reason, CancellationToken ct)
    {
        row.Status = status;
        row.NextAttemptAt = null;
        row.LastError = Truncate(reason, 1024);
        await db.SaveChangesAsync(ct);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    [LoggerMessage(EventId = 5150, Level = LogLevel.Information,
        Message = "Sent {PublicRef} ({Kind}, {Language}).")]
    private static partial void LogSent(ILogger logger, string publicRef, EmailKind kind, string language);

    [LoggerMessage(EventId = 5151, Level = LogLevel.Warning,
        Message = "Recovered {Recovered} expired lease(s); failed {Dead} row(s) whose lease expired twice.")]
    private static partial void LogLeasesRecovered(ILogger logger, int recovered, int dead);

    [LoggerMessage(EventId = 5152, Level = LogLevel.Error,
        Message = "Outbox row {PublicRef} could not be rendered ({Problem}); it was not sent.")]
    private static partial void LogNotRendered(ILogger logger, string publicRef, string problem);

    [LoggerMessage(EventId = 5153, Level = LogLevel.Error,
        Message = "Outbox row {PublicRef} permanently failed ({Code}): {Diagnostic}. No suppression.")]
    private static partial void LogPermanent(ILogger logger, string publicRef, string code, string diagnostic);

    [LoggerMessage(EventId = 5154, Level = LogLevel.Error,
        Message = "Abandoned {PublicRef} to {Email} after the full retry schedule.")]
    private static partial void LogAbandoned(ILogger logger, string publicRef, string email);

    [LoggerMessage(EventId = 5155, Level = LogLevel.Error, Message = "{Problem}")]
    private static partial void LogVerpDisabled(ILogger logger, string problem);
}
