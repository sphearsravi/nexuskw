using System.Collections.Concurrent;
using Nexus.Application.Common;
using Nexus.Application.Delivery;
using Nexus.Application.Email;
using Nexus.Domain;
using Nexus.Domain.Entities;

namespace Nexus.IntegrationTests;

/// <summary>
/// Stage C owns <c>SettingsReader</c>. Stage D only needs the contract, so the tests
/// supply it: an in-memory dictionary that clamps and never throws, like the real one.
/// </summary>
internal sealed class StageDSettings : ISettingsReader
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    public StageDSettings Set(string key, string value)
    {
        _values[key] = value;
        return this;
    }

    public Task<string> GetStringAsync(string key, string fallback, CancellationToken ct = default) =>
        Task.FromResult(_values.TryGetValue(key, out var value) ? value : fallback);

    public Task<int> GetIntAsync(string key, int fallback, int min, int max, CancellationToken ct = default) =>
        Task.FromResult(Math.Clamp(
            _values.TryGetValue(key, out var raw) && int.TryParse(raw, out var parsed) ? parsed : fallback,
            min, max));

    public Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken ct = default) =>
        Task.FromResult(_values.TryGetValue(key, out var raw) && bool.TryParse(raw, out var parsed)
            ? parsed
            : fallback);

    public Task<long> GetLongAsync(string key, long fallback, long min, long max, CancellationToken ct = default) =>
        Task.FromResult(Math.Clamp(
            _values.TryGetValue(key, out var raw) && long.TryParse(raw, out var parsed) ? parsed : fallback,
            min, max));

    public Task SetAsync(string key, string value, bool isSecret, string? updatedBy, CancellationToken ct = default)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Stage C is building the real delivery service in a checkout this stage cannot see, so
/// the renderer is tested against the interface. The only member Stage D calls is
/// <see cref="UnprotectToken"/>; the rest throw loudly rather than quietly returning
/// something that would make a test pass for the wrong reason.
/// </summary>
internal sealed class StageDDelivery : IDeliveryService
{
    /// <summary>Grant id to plaintext token, as Stage C's DataProtection round-trip would give it.</summary>
    public Dictionary<int, string> Tokens { get; } = [];

    public string? UnprotectToken(DownloadGrant grant) =>
        Tokens.TryGetValue(grant.Id, out var token) ? token : null;

    public Task<IReadOnlyList<IssuedGrant>> IssueGrantsForOrderAsync(int orderId, CancellationToken ct = default) =>
        throw new NotSupportedException("Stage C owns grant issue.");

    public Task<DownloadDecision> AuthoriseDownloadAsync(
        string token, string? ipPrefix, string? userAgent, CancellationToken ct = default) =>
        throw new NotSupportedException("Stage C owns download authorisation.");

    public Task<int> RevokeOrderGrantsAsync(int orderId, RevocationReason reason, CancellationToken ct = default) =>
        throw new NotSupportedException("Stage C owns revocation.");

    public Task<(IssuedGrant? Grant, string? Problem)> ReIssueAsync(int grantId, CancellationToken ct = default) =>
        throw new NotSupportedException("Stage C owns re-issue.");
}

/// <summary>A transport that records rather than sends, and fails on demand.</summary>
internal sealed class StageDRecordingSender : IEmailSender
{
    public List<OutgoingEmail> Sent { get; } = [];

    public Func<OutgoingEmail, EmailSendResult>? Respond { get; set; }

    public Task<EmailSendResult> SendAsync(OutgoingEmail message, CancellationToken ct = default)
    {
        var result = Respond?.Invoke(message) ?? new EmailSendResult(EmailSendOutcome.Sent, "recorded", "250");
        if (result.Outcome == EmailSendOutcome.Sent) Sent.Add(message);
        return Task.FromResult(result);
    }
}

/// <summary>An outbox that cannot write, to prove a paid order survives one that cannot.</summary>
internal sealed class ThrowingOutbox : IEmailOutbox
{
    public Task<OutboxEmail> EnqueueAsync(
        EmailKind kind, int? orderId, int? customerId, string toEmail, string language,
        string idempotencyKey, CancellationToken ct = default) =>
        throw new InvalidOperationException("the outbox is broken");
}

internal sealed class StageDClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}
