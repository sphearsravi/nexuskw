using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexus.Application.Common;
using Nexus.Application.Delivery;
using Nexus.Domain;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Persistence;
using Nexus.Infrastructure.Settings;

namespace Nexus.Infrastructure.Delivery;

/// <summary>
/// Issues and polices download rights. Everything a buyer paid for passes through here,
/// and so does everything a stranger holding a forwarded link tries.
/// </summary>
public sealed class DeliveryService(
    NexusDbContext db,
    IPdfLibrary library,
    IPdfStamper stamper,
    ISettingsReader settings,
    IDataProtectionProvider protection,
    ILogger<DeliveryService> log) : IDeliveryService, IGrantStatusReader
{
    /// <summary>
    /// Versioned, because a purpose string is part of the ciphertext's identity: a
    /// future v2 must fail to unprotect v1 loudly rather than return rubbish.
    /// </summary>
    public const string ProtectorPurpose = "Nexus.Delivery.GrantToken.v1";

    private readonly IDataProtector _protector = protection.CreateProtector(ProtectorPurpose);

    public async Task<IReadOnlyList<IssuedGrant>> IssueGrantsForOrderAsync(
        int orderId, CancellationToken ct = default)
    {
        var order = await db.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw new InvalidOperationException($"Order {orderId} does not exist.");

        // Idempotent by read, under the caller's lock on the order row (the unique index
        // on (OrderId, BookId, Sequence) stays as the schema invariant, but catching a
        // constraint violation inside the payment transaction poisons it — this returns
        // before there is anything to catch).
        var existing = await db.DownloadGrants
            .Where(g => g.OrderId == orderId)
            .ToListAsync(ct);

        if (existing.Count > 0)
        {
            return existing
                .Select(g => new IssuedGrant(g, UnprotectToken(g) ?? ""))
                .ToList();
        }

        var maxDownloads = await settings.GetIntAsync(
            SettingKeys.MaxDownloads, DeliveryDefaults.MaxDownloads,
            DeliveryDefaults.MaxDownloadsMin, DeliveryDefaults.MaxDownloadsMax, ct);

        var expiryDays = await settings.GetIntAsync(
            SettingKeys.LinkExpiryDays, DeliveryDefaults.LinkExpiryDays,
            DeliveryDefaults.LinkExpiryDaysMin, DeliveryDefaults.LinkExpiryDaysMax, ct);

        var bookIds = order.Items.Select(i => i.BookId).ToArray();
        var publishState = await db.Books
            .Where(b => bookIds.Contains(b.Id))
            .Select(b => new { b.Id, b.IsPublished })
            .ToDictionaryAsync(b => b.Id, b => b.IsPublished, ct);

        var now = DateTimeOffset.UtcNow;
        var issued = new List<IssuedGrant>();

        foreach (var item in order.Items)
        {
            // A title withdrawn between order and capture: the money has moved, so the
            // order is still paid, but we do not hand over something we may no longer
            // have the rights to. The operator closes it by re-issue or refund.
            if (!publishState.GetValueOrDefault(item.BookId))
            {
                order.ReviewReason ??= $"withdrawn-title:{item.BookId}";
                log.LogWarning(
                    "Grant withheld on order {OrderId}: book {BookId} is no longer published.",
                    orderId, item.BookId);
                continue;
            }

            var token = DownloadToken.New();
            var grant = new DownloadGrant
            {
                OrderId = orderId,
                BookId = item.BookId,
                Sequence = 1,
                TokenHash = DownloadToken.Hash(token),
                TokenProtected = _protector.Protect(Encoding.ASCII.GetBytes(token)),
                // Snapshotted, not read live at download: an operator who shortens the
                // window must not shrink an allowance somebody already paid for.
                MaxDownloads = maxDownloads,
                UsedCount = 0,
                ExpiresAt = now.AddDays(expiryDays),
                State = GrantState.Active,
                CreatedAt = now
            };

            db.DownloadGrants.Add(grant);
            issued.Add(new IssuedGrant(grant, token));
        }

        await db.SaveChangesAsync(ct);
        return issued;
    }

    public async Task<DownloadDecision> AuthoriseDownloadAsync(
        string token, string? ipPrefix, string? userAgent, CancellationToken ct = default)
    {
        // Shape first, so an enumeration attempt costs a string comparison rather than
        // an index seek. Unknown tokens are never written to DownloadLogs either — a row
        // per guess is an unbounded-growth primitive against a shared-hosting disk.
        if (!DownloadToken.LooksWellFormed(token)) return NotFound();

        var hash = DownloadToken.Hash(token);
        var grant = await db.DownloadGrants
            .Include(g => g.Order)
            .Include(g => g.Book)
            .FirstOrDefaultAsync(g => g.TokenHash == hash, ct);

        if (grant?.Order is null || grant.Book is null) return NotFound();

        // A refunded order's grants are already revoked; this is the second lock on the
        // same door. A partial refund deliberately leaves delivery alone.
        if (grant.Order.Status is not (OrderStatus.Paid or OrderStatus.PartiallyRefunded))
            return await RefuseAsync(grant, DownloadOutcome.Revoked, ipPrefix, userAgent, ct);

        var now = DateTimeOffset.UtcNow;
        if (grant.ServeBlocker(now) is { } blocker)
            return await RefuseAsync(grant, Map(blocker), ipPrefix, userAgent, ct);

        var sourcePath = grant.Book.PdfStorageKey is { } key ? library.ResolvePath(key) : null;
        if (sourcePath is null)
        {
            // A published book whose file has vanished is an operator incident, not a
            // 404 at the customer. Nothing is counted against them.
            log.LogError(
                "Book {BookId} is sold but its file is missing ({Key}); grant {GrantId} cannot be served.",
                grant.BookId, grant.Book.PdfStorageKey, grant.Id);
            return await RefuseAsync(grant, DownloadOutcome.StampFailed, ipPrefix, userAgent, ct);
        }

        // The bytes are produced BEFORE the counter moves. The frozen contract has no
        // completion callback, so a download that was never produced must not be able to
        // consume an allowance — and this ordering makes that structural rather than a
        // compensating UPDATE that has to be got right.
        var servePath = sourcePath;
        var watermark = await settings.GetBoolAsync(
            SettingKeys.WatermarkEnabled, DeliveryDefaults.WatermarkEnabled, ct);

        if (watermark)
        {
            try
            {
                servePath = await stamper.StampedCopyAsync(
                    grant, sourcePath, grant.Order.BuyerNameSnapshot,
                    grant.Order.BuyerEmailSnapshot, grant.Order.OrderNumber, ct);
            }
            catch (PdfBusyException)
            {
                return await RefuseAsync(grant, DownloadOutcome.Busy, ipPrefix, userAgent, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Delivering the product beats delivering the watermark. The buyer gets
                // their book; the operator gets an Error they can act on.
                log.LogError(ex, "Stamping grant {GrantId} failed; serving the source unstamped.", grant.Id);
                servePath = sourcePath;
            }
        }

        var consumed = await ConsumeAsync(grant.Id, now, ct);
        if (consumed == 0)
        {
            // Somebody else took the last one between the check and here. Re-read so the
            // customer is told the truth rather than "try again".
            await db.Entry(grant).ReloadAsync(ct);
            var reason = grant.ServeBlocker(DateTimeOffset.UtcNow) ?? "exhausted";
            return await RefuseAsync(grant, Map(reason), ipPrefix, userAgent, ct);
        }

        await FlagIfSharedAsync(grant, ipPrefix, ct);
        await LogAsync(grant.Id, DownloadOutcome.Served, ipPrefix, userAgent, ct);

        var fileName = $"{LibraryPaths.Slug(grant.Book.TitleEn, grant.BookId)}-{grant.Order.OrderNumber}.pdf";
        return new DownloadDecision(DownloadOutcome.Served, grant, servePath, fileName);
    }

    public async Task<GrantStatusDto?> DescribeAsync(string token, CancellationToken ct = default)
    {
        if (!DownloadToken.LooksWellFormed(token)) return null;

        var hash = DownloadToken.Hash(token);
        var row = await db.DownloadGrants
            .Where(g => g.TokenHash == hash)
            .Select(g => new
            {
                g.BookId,
                g.UsedCount,
                g.MaxDownloads,
                g.ExpiresAt,
                g.State,
                OrderStatus = g.Order!.Status,
                OrderNumber = g.Order!.OrderNumber,
                g.Book!.TitleEn,
                g.Book!.TitleAr,
                g.Book!.PdfSizeBytes
            })
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

        if (row is null) return null;

        var now = DateTimeOffset.UtcNow;
        var state =
            row.OrderStatus is not (OrderStatus.Paid or OrderStatus.PartiallyRefunded) ? "revoked"
            : row.State == GrantState.Revoked ? "revoked"
            : row.State == GrantState.Exhausted || row.UsedCount >= row.MaxDownloads ? "exhausted"
            // Expired is a materialised state for the admin filter and is never trusted
            // at read time: a grant that lapsed four minutes ago has not been swept yet.
            : now >= row.ExpiresAt ? "expired"
            : "active";

        return new GrantStatusDto(
            row.BookId, row.TitleEn, row.TitleAr, row.OrderNumber,
            row.UsedCount, row.MaxDownloads, Math.Max(0, row.MaxDownloads - row.UsedCount),
            row.ExpiresAt, state, row.PdfSizeBytes, $"/download/{token}");
    }

    public async Task<int> RevokeOrderGrantsAsync(
        int orderId, RevocationReason reason, CancellationToken ct = default)
    {
        // The database state change and NOTHING else. Deleting the cached stamped files
        // is a separate, non-transactional step: a buyer mid-download holds the file, the
        // delete throws, the transaction rolls back, and the refunded order stays Paid
        // with every grant active. Revocation is authoritative here; the cache purge is
        // housekeeping and must never be able to fail the revoke.
        var now = DateTimeOffset.UtcNow;

        return await db.DownloadGrants
            .Where(g => g.OrderId == orderId && g.State != GrantState.Revoked)
            .ExecuteUpdateAsync(set => set
                .SetProperty(g => g.State, GrantState.Revoked)
                .SetProperty(g => g.RevocationReason, reason)
                .SetProperty(g => g.RevokedAt, now), ct);
    }

    /// <summary>
    /// The cache purge that <see cref="RevokeOrderGrantsAsync"/> deliberately does not
    /// do. Best effort, after the commit, outside any transaction.
    /// </summary>
    public async Task PurgeStampedAsync(int orderId, CancellationToken ct = default)
    {
        var grants = await db.DownloadGrants
            .Where(g => g.OrderId == orderId)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var grant in grants)
        {
            try
            {
                await stamper.EvictAsync(grant, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Stamped cache for grant {GrantId} survives; the nightly pass will take it.", grant.Id);
            }
        }
    }

    public async Task<(IssuedGrant? Grant, string? Problem)> ReIssueAsync(
        int grantId, CancellationToken ct = default)
    {
        var old = await db.DownloadGrants
            .Include(g => g.Order)
            .FirstOrDefaultAsync(g => g.Id == grantId, ct);

        if (old?.Order is null) return (null, "not_found");
        if (old.Order.Status is not (OrderStatus.Paid or OrderStatus.PartiallyRefunded))
            return (null, "order_not_paid");

        var limit = await settings.GetIntAsync(
            SettingKeys.ReissueLimitPerItem, DeliveryDefaults.ReissueLimitPerItem,
            DeliveryDefaults.ReissueLimitPerItemMin, DeliveryDefaults.ReissueLimitPerItemMax, ct);

        var siblings = await db.DownloadGrants
            .Where(g => g.OrderId == old.OrderId && g.BookId == old.BookId)
            .ToListAsync(ct);

        if (siblings.Count >= limit) return (null, "reissue_limit");

        var maxDownloads = await settings.GetIntAsync(
            SettingKeys.MaxDownloads, DeliveryDefaults.MaxDownloads,
            DeliveryDefaults.MaxDownloadsMin, DeliveryDefaults.MaxDownloadsMax, ct);

        var expiryDays = await settings.GetIntAsync(
            SettingKeys.LinkExpiryDays, DeliveryDefaults.LinkExpiryDays,
            DeliveryDefaults.LinkExpiryDaysMin, DeliveryDefaults.LinkExpiryDaysMax, ct);

        var now = DateTimeOffset.UtcNow;
        var token = DownloadToken.New();
        var fresh = new DownloadGrant
        {
            OrderId = old.OrderId,
            BookId = old.BookId,
            Sequence = siblings.Max(g => g.Sequence) + 1,
            TokenHash = DownloadToken.Hash(token),
            TokenProtected = _protector.Protect(Encoding.ASCII.GetBytes(token)),
            MaxDownloads = maxDownloads,
            ExpiresAt = now.AddDays(expiryDays),
            State = GrantState.Active,
            CreatedAt = now
        };

        // The old row stays as history and the old link dies with it. There is no
        // un-revoke: an audit trail you can edit is not one.
        if (old.State != GrantState.Revoked)
        {
            old.State = GrantState.Revoked;
            old.RevocationReason = RevocationReason.ReIssued;
            old.RevokedAt = now;
        }

        db.DownloadGrants.Add(fresh);
        await db.SaveChangesAsync(ct);

        return (new IssuedGrant(fresh, token), null);
    }

    public string? UnprotectToken(DownloadGrant grant)
    {
        if (grant.TokenProtected is not { Length: > 0 } sealedToken) return null;

        try
        {
            return Encoding.ASCII.GetString(_protector.Unprotect(sealedToken));
        }
        catch (Exception ex)
        {
            // Key ring rotated away, or the row was copied between environments. Never
            // log the ciphertext, and never throw at a caller trying to send an email.
            log.LogError(ex, "Could not unprotect the token on grant {GrantId}.", grant.Id);
            return null;
        }
    }

    /// <summary>
    /// State, expiry and count are checked and the counter is moved in one statement.
    /// <para>
    /// Two concurrent requests on a three-use grant cannot yield four downloads because
    /// there is no read to lose: the second <c>UPDATE</c> blocks on the row lock the
    /// first one holds, then re-evaluates its own <c>WHERE</c> against the committed
    /// value and matches nothing. The guarantee is the engine's write locking, not
    /// application ordering.
    /// </para>
    /// </summary>
    private async Task<int> ConsumeAsync(int grantId, DateTimeOffset now, CancellationToken ct) =>
        await db.DownloadGrants
            .Where(g => g.Id == grantId
                     && g.State == GrantState.Active
                     && g.ExpiresAt > now
                     && g.UsedCount < g.MaxDownloads)
            .ExecuteUpdateAsync(set => set
                .SetProperty(g => g.UsedCount, g => g.UsedCount + 1)
                .SetProperty(g => g.State,
                    g => g.UsedCount + 1 >= g.MaxDownloads ? GrantState.Exhausted : g.State), ct);

    private async Task FlagIfSharedAsync(DownloadGrant grant, string? ipPrefix, CancellationToken ct)
    {
        if (ipPrefix is null || grant.FlaggedAt is not null) return;

        var seen = await db.DownloadLogs
            .Where(l => l.GrantId == grant.Id && l.IpPrefix != null)
            .Select(l => l.IpPrefix!)
            .Distinct()
            .ToListAsync(ct);

        if (seen.Contains(ipPrefix)) return;

        var threshold = await settings.GetIntAsync(
            SettingKeys.DistinctIpThreshold, DeliveryDefaults.DistinctIpThreshold,
            DeliveryDefaults.DistinctIpThresholdMin, DeliveryDefaults.DistinctIpThresholdMax, ct);

        if (seen.Count + 1 < threshold) return;

        // Flagging never blocks. Kuwait's mobile networks NAT heavily in both
        // directions, so an automatic block refuses honest buyers on a phone far more
        // often than it stops a sharer. Revocation stays a human decision.
        grant.FlaggedAt = DateTimeOffset.UtcNow;
        log.LogWarning(
            "Grant {GrantId} used from {Count} distinct address prefixes; flagged for review.",
            grant.Id, seen.Count + 1);
    }

    private async Task<DownloadDecision> RefuseAsync(
        DownloadGrant grant, DownloadOutcome outcome, string? ipPrefix, string? userAgent, CancellationToken ct)
    {
        await LogAsync(grant.Id, outcome, ipPrefix, userAgent, ct);
        return new DownloadDecision(outcome, grant, null, null);
    }

    private async Task LogAsync(
        int grantId, DownloadOutcome outcome, string? ipPrefix, string? userAgent, CancellationToken ct)
    {
        db.DownloadLogs.Add(new DownloadLog
        {
            GrantId = grantId,
            AtUtc = DateTimeOffset.UtcNow,
            IpPrefix = ipPrefix,
            UserAgent = userAgent is { Length: > 512 } ua ? ua[..512] : userAgent,
            Outcome = outcome
        });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// <c>Grant is null</c> is the whole not-found signal: the frozen
    /// <see cref="DownloadDecision"/> has no NotFound outcome, and the controller checks
    /// the grant before it reads the outcome.
    /// </summary>
    private static DownloadDecision NotFound() => new(DownloadOutcome.Expired, null, null, null);

    private static DownloadOutcome Map(string blocker) => blocker switch
    {
        "revoked" => DownloadOutcome.Revoked,
        "exhausted" => DownloadOutcome.Exhausted,
        _ => DownloadOutcome.Expired
    };
}
