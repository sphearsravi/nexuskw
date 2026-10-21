using Microsoft.EntityFrameworkCore;
using Nexus.Application.Common;
using Nexus.Application.Delivery;
using Nexus.Domain;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Admin;

/// <summary>
/// The delivery log and the two actions that resolve a download complaint: revoke, and
/// re-issue.
/// <para>
/// <b>No endpoint here ever returns a token.</b> Not the plaintext, not the hash, not the
/// sealed bytes. An operator screen that shows a working download link turns a compromised
/// staff account into free copies of every book in the shop, and the operator does not
/// need one: re-issue mails a fresh link to the buyer.
/// </para>
/// </summary>
public sealed class DownloadAdminService(
    NexusDbContext db,
    IDeliveryService delivery,
    IAuditLog audit)
{
    public const int MaxPageSize = 100;

    public async Task<Page<AdminGrantRow>> ListAsync(
        string? q, string? state, bool flaggedOnly, int page, int pageSize, CancellationToken ct = default)
    {
        var query = db.DownloadGrants.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(state) &&
            Enum.TryParse<GrantState>(state, ignoreCase: true, out var wanted))
            query = query.Where(g => g.State == wanted);

        if (flaggedOnly) query = query.Where(g => g.FlaggedAt != null);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim();
            query = query.Where(g =>
                g.Order!.OrderNumber.Contains(needle) ||
                g.Order.BuyerEmailSnapshot.Contains(needle) ||
                g.Book!.TitleEn.Contains(needle) ||
                g.Book.TitleAr.Contains(needle));
        }

        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var skip = Math.Max(0, page - 1) * size;

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(g => g.CreatedAt).ThenByDescending(g => g.Id)
            .Skip(skip).Take(size)
            .Select(g => new AdminGrantRow(
                g.Id,
                g.OrderId,
                g.Order!.OrderNumber,
                g.Order.BuyerEmailSnapshot,
                g.BookId,
                g.Book!.TitleEn,
                g.Book.TitleAr,
                g.Sequence,
                g.UsedCount,
                g.MaxDownloads,
                g.State.ToString(),
                g.RevocationReason.ToString(),
                g.ExpiresAt,
                g.CreatedAt,
                g.FlaggedAt,
                // DL-8: the count of distinct addresses is what tells an operator a link
                // is being passed around, and it is cheaper to read than the log itself.
                g.Logs.Where(l => l.IpPrefix != null).Select(l => l.IpPrefix).Distinct().Count(),
                g.Logs.OrderByDescending(l => l.AtUtc).Select(l => l.IpPrefix).FirstOrDefault(),
                g.Logs.Max(l => (DateTimeOffset?)l.AtUtc)))
            .ToListAsync(ct);

        return new Page<AdminGrantRow>(total, Math.Max(1, page), size, rows);
    }

    public async Task<IReadOnlyList<AdminDownloadLogRow>> LogAsync(
        int grantId, int take = 100, CancellationToken ct = default) =>
        await db.DownloadLogs.AsNoTracking()
            .Where(l => l.GrantId == grantId)
            .OrderByDescending(l => l.AtUtc)
            .Take(Math.Clamp(take, 1, 500))
            .Select(l => new AdminDownloadLogRow(
                l.Id, l.AtUtc, l.IpPrefix, l.UserAgent, l.Outcome.ToString(), l.BytesSent))
            .ToListAsync(ct);

    /// <summary>
    /// Revokes one grant. <c>IDeliveryService.RevokeOrderGrantsAsync</c> is the refund
    /// path and takes the whole order; an operator acting on a shared link needs to kill
    /// that link and leave the rest of the purchase alone.
    /// </summary>
    public async Task<(AdminGrantRow? Grant, AdminProblem? Problem)> RevokeAsync(
        int grantId, string? reason, CancellationToken ct = default)
    {
        var grant = await db.DownloadGrants.FirstOrDefaultAsync(g => g.Id == grantId, ct);
        if (grant is null) return (null, new AdminProblem("grant_not_found"));
        if (grant.State == GrantState.Revoked) return (null, new AdminProblem("grant_already_revoked"));

        var before = new { State = grant.State.ToString(), grant.UsedCount, grant.ExpiresAt };

        grant.State = GrantState.Revoked;
        grant.RevocationReason = ParseReason(reason);
        grant.RevokedAt = DateTimeOffset.UtcNow;
        // The one copy of a live link we hold goes with it; there is no un-revoke.
        grant.TokenProtected = null;

        await audit.RecordAsync("DownloadGrant", grant.Id, "grant.revoke", before, new
        {
            State = nameof(GrantState.Revoked),
            Reason = grant.RevocationReason.ToString()
        }, ct);

        await db.SaveChangesAsync(ct);
        return (await OneAsync(grantId, ct), null);
    }

    /// <summary>
    /// Re-issues through <see cref="IDeliveryService.ReIssueAsync"/>, which owns the
    /// re-issue limit and revokes the old grant. The new token is never returned here —
    /// the buyer gets it by email.
    /// </summary>
    public async Task<(AdminGrantRow? Grant, AdminProblem? Problem)> ReIssueAsync(
        int grantId, CancellationToken ct = default)
    {
        var (issued, problem) = await delivery.ReIssueAsync(grantId, ct);
        if (issued is null) return (null, new AdminProblem(problem ?? "reissue_failed"));

        await audit.RecordAsync("DownloadGrant", grantId, "grant.reissue",
            new { GrantId = grantId },
            new { NewGrantId = issued.Grant.Id, issued.Grant.Sequence, issued.Grant.ExpiresAt }, ct);

        await db.SaveChangesAsync(ct);
        return (await OneAsync(issued.Grant.Id, ct), null);
    }

    private async Task<AdminGrantRow?> OneAsync(int grantId, CancellationToken ct) =>
        await db.DownloadGrants.AsNoTracking()
            .Where(g => g.Id == grantId)
            .Select(g => new AdminGrantRow(
                g.Id, g.OrderId, g.Order!.OrderNumber, g.Order.BuyerEmailSnapshot,
                g.BookId, g.Book!.TitleEn, g.Book.TitleAr, g.Sequence,
                g.UsedCount, g.MaxDownloads, g.State.ToString(), g.RevocationReason.ToString(),
                g.ExpiresAt, g.CreatedAt, g.FlaggedAt,
                g.Logs.Where(l => l.IpPrefix != null).Select(l => l.IpPrefix).Distinct().Count(),
                g.Logs.OrderByDescending(l => l.AtUtc).Select(l => l.IpPrefix).FirstOrDefault(),
                g.Logs.Max(l => (DateTimeOffset?)l.AtUtc)))
            .FirstOrDefaultAsync(ct);

    private static RevocationReason ParseReason(string? reason) =>
        Enum.TryParse<RevocationReason>(reason, ignoreCase: true, out var parsed) && parsed != RevocationReason.None
            ? parsed
            : RevocationReason.OperatorRequest;
}
