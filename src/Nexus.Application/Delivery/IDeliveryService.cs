using Nexus.Domain;
using Nexus.Domain.Entities;

namespace Nexus.Application.Delivery;

/// <summary>A grant plus its plaintext token — the only moment the token exists in the open.</summary>
public sealed record IssuedGrant(DownloadGrant Grant, string Token);

/// <summary>What a download attempt produced.</summary>
public sealed record DownloadDecision(
    DownloadOutcome Outcome,
    DownloadGrant? Grant,
    string? AbsolutePath,
    string? SuggestedFileName);

/// <summary>Issues and polices download rights.</summary>
public interface IDeliveryService
{
    /// <summary>
    /// Creates one grant per item on a paid order. Called from inside the payment
    /// transaction, so a paid order cannot exist without its grants.
    /// Idempotent: called twice for the same order, it returns the existing grants.
    /// </summary>
    Task<IReadOnlyList<IssuedGrant>> IssueGrantsForOrderAsync(int orderId, CancellationToken ct = default);

    /// <summary>
    /// Resolves a plaintext token and decides whether to serve. Checks state, expiry and
    /// count — the existence of a valid token is not enough — and increments the counter
    /// atomically, so two concurrent requests on a three-use grant cannot yield four
    /// downloads.
    /// </summary>
    Task<DownloadDecision> AuthoriseDownloadAsync(
        string token, string? ipPrefix, string? userAgent, CancellationToken ct = default);

    /// <summary>
    /// Revokes every grant on an order. The state change only — deleting cached stamped
    /// files is a separate, non-transactional step, because filesystem I/O inside a
    /// database transaction is how a refund ends up half-applied.
    /// </summary>
    Task<int> RevokeOrderGrantsAsync(int orderId, RevocationReason reason, CancellationToken ct = default);

    /// <summary>Issues a fresh grant for an expired or exhausted one, within the re-issue limit.</summary>
    Task<(IssuedGrant? Grant, string? Problem)> ReIssueAsync(int grantId, CancellationToken ct = default);

    /// <summary>Recovers the plaintext token for an active grant, for the email dispatcher only.</summary>
    string? UnprotectToken(DownloadGrant grant);
}

/// <summary>
/// The PDF store. Files live outside the web root; nothing here takes a client-supplied
/// path or filename.
/// </summary>
public interface IPdfLibrary
{
    /// <summary>
    /// Validates by magic bytes and structure, not by extension, then stores under a
    /// server-derived key. Returns the key and the facts the catalogue records.
    /// </summary>
    Task<(string StorageKey, string Sha256, long SizeBytes, int PageCount)> StoreAsync(
        int bookId, Stream content, string originalFileName, CancellationToken ct = default);

    /// <summary>Absolute path for a stored key, or null when the file is missing.</summary>
    string? ResolvePath(string storageKey);

    Task DeleteAsync(string storageKey, CancellationToken ct = default);

    /// <summary>Writes a small valid stub so development has something to serve. Always marked a placeholder.</summary>
    Task<(string StorageKey, string Sha256, long SizeBytes, int PageCount)> WritePlaceholderAsync(
        int bookId, CancellationToken ct = default);
}

/// <summary>
/// Stamps a buyer's details into a copy of a PDF. The main deterrent against casual
/// sharing is that it is attributable, not that it is impossible.
/// </summary>
public interface IPdfStamper
{
    /// <summary>
    /// Returns a path to a stamped copy, building and caching it on first use so a
    /// three-hundred-page book is stamped once per grant rather than once per download.
    /// </summary>
    Task<string> StampedCopyAsync(
        DownloadGrant grant, string sourcePath, string buyerName, string buyerEmail,
        string orderNumber, CancellationToken ct = default);

    Task EvictAsync(DownloadGrant grant, CancellationToken ct = default);
}
