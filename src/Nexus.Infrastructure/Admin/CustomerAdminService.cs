using System.Globalization;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Nexus.Application.Common;
using Nexus.Domain;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Admin;

/// <summary>
/// Customers: who they are, what they bought, what they are worth, and how to erase them.
/// </summary>
public sealed class CustomerAdminService(NexusDbContext db, IAuditLog audit)
{
    public const int MaxPageSize = 100;

    /// <summary>
    /// The domain an erased customer's address is moved to. <c>.invalid</c> is reserved by
    /// RFC 2606 and can never be registered, so nothing we delete can later be mailed to
    /// a real person by accident.
    /// </summary>
    public const string ErasedDomain = "removed.invalid";

    public async Task<Page<AdminCustomerRow>> ListAsync(
        string? q, int page, int pageSize, CancellationToken ct = default)
    {
        var query = db.Customers.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim();
            query = query.Where(c => c.Email.Contains(needle) || c.Name.Contains(needle));
        }

        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var skip = Math.Max(0, page - 1) * size;

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id)
            .Skip(skip).Take(size)
            .Select(Projection)
            .ToListAsync(ct);

        return new Page<AdminCustomerRow>(total, Math.Max(1, page), size, rows);
    }

    public async Task<AdminCustomerDetail?> GetAsync(int id, CancellationToken ct = default)
    {
        var row = await db.Customers.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(Projection)
            .FirstOrDefaultAsync(ct);

        if (row is null) return null;

        var orders = await db.Orders.AsNoTracking()
            .Where(o => o.CustomerId == id)
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => new AdminOrderRow(
                o.Id, o.OrderNumber, o.BuyerNameSnapshot, o.BuyerEmailSnapshot,
                o.Status.ToString(), o.Language, o.TotalKwd, o.RefundedTotalKwd,
                o.Items.Count, o.PaymentMethod, o.CreatedAt, o.PaidAt))
            .ToListAsync(ct);

        return new AdminCustomerDetail(row, orders);
    }

    /// <summary>
    /// Delete-my-data. The person is erased; the money is not.
    /// <para>
    /// Orders are an accounting record and are kept, with their buyer snapshots
    /// overwritten — that is where the personal data on an order actually lives. Download
    /// grants are revoked, because leaving a live link to a watermarked PDF bearing a name
    /// we have just promised to erase would make the erasure a fiction. The download log
    /// keeps its rows and loses its addresses, so the counts an auditor needs survive and
    /// the identifiers do not.
    /// </para>
    /// </summary>
    public async Task<(bool Done, AdminProblem? Problem)> EraseAsync(
        int id, CancellationToken ct = default)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (customer is null) return (false, new AdminProblem("customer_not_found"));

        var placeholder = string.Create(CultureInfo.InvariantCulture, $"erased-{id}@{ErasedDomain}");
        if (string.Equals(customer.Email, placeholder, StringComparison.Ordinal))
            return (false, new AdminProblem("customer_already_erased"));

        var before = new { customer.Name, customer.Email, customer.Country, customer.IdentityUserId };

        customer.Name = "Erased";
        customer.Email = placeholder;
        customer.NormalisedEmail = Customer.Normalise(placeholder);
        customer.IdentityUserId = null;
        customer.UpdatedAt = DateTimeOffset.UtcNow;

        /* The login goes too. Leaving it would leave the address we have just promised to
         * erase sitting in AspNetUsers, still able to sign in and still able to receive a
         * password-reset mail. The FK from NexusUser to Customer is Restrict, so the
         * login must be removed before the customer row is touched, not after. */
        var logins = await db.Users.Where(u => u.CustomerId == id).ToListAsync(ct);
        db.Users.RemoveRange(logins);

        var orders = await db.Orders.Where(o => o.CustomerId == id).ToListAsync(ct);
        foreach (var order in orders)
        {
            order.BuyerNameSnapshot = "Erased";
            order.BuyerEmailSnapshot = placeholder;
            order.UpdatedAt = DateTimeOffset.UtcNow;
        }

        var orderIds = orders.Select(o => o.Id).ToList();

        var grants = await db.DownloadGrants
            .Where(g => orderIds.Contains(g.OrderId) && g.State != GrantState.Revoked)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var grant in grants)
        {
            grant.State = GrantState.Revoked;
            grant.RevocationReason = RevocationReason.CustomerDataDeleted;
            grant.RevokedAt = now;
            // The sealed token is the one copy of a live download link we hold.
            grant.TokenProtected = null;
        }

        var grantIds = await db.DownloadGrants
            .Where(g => orderIds.Contains(g.OrderId))
            .Select(g => g.Id)
            .ToListAsync(ct);

        var logs = await db.DownloadLogs.Where(l => grantIds.Contains(l.GrantId)).ToListAsync(ct);
        foreach (var entry in logs)
        {
            entry.IpPrefix = null;
            entry.UserAgent = null;
        }

        // Queued mail would deliver to the address we have just erased.
        var pending = await db.OutboxEmails
            .Where(e => e.CustomerId == id && (e.Status == EmailStatus.Queued || e.Status == EmailStatus.Failed))
            .ToListAsync(ct);

        foreach (var mail in pending)
        {
            mail.Status = EmailStatus.Abandoned;
            mail.LastError = "customer data erased";
        }

        foreach (var mail in await db.OutboxEmails.Where(e => e.CustomerId == id).ToListAsync(ct))
            mail.ToEmail = placeholder;

        await audit.RecordAsync("Customer", id, "customer.erase", before, new
        {
            Orders = orders.Count,
            LoginsRemoved = logins.Count,
            GrantsRevoked = grants.Count,
            LogsScrubbed = logs.Count,
            EmailsAbandoned = pending.Count
        }, ct);

        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    /// <summary>
    /// Lifetime value is <b>captured minus refunded</b>, over paid orders only. Counting a
    /// Pending order would make the figure a hope rather than a fact.
    /// </summary>
    private static readonly Expression<Func<Customer, AdminCustomerRow>> Projection = c => new AdminCustomerRow(
        c.Id,
        c.Name,
        c.Email,
        c.Country,
        c.PreferredLanguage,
        c.IdentityUserId != null,
        c.Orders.Count,
        c.Orders.Count(o => o.Status == OrderStatus.Paid || o.Status == OrderStatus.PartiallyRefunded),
        c.Orders
            .Where(o => o.Status == OrderStatus.Paid || o.Status == OrderStatus.PartiallyRefunded)
            .Sum(o => o.TotalCharged - o.RefundedTotalKwd),
        c.CreatedAt,
        c.Orders.Max(o => (DateTimeOffset?)o.CreatedAt));
}
