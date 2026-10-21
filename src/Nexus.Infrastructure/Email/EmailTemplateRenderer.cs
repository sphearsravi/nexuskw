using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexus.Application.Common;
using Nexus.Application.Delivery;
using Nexus.Application.Email;
using Nexus.Domain;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Email;

/// <summary>
/// Builds the message at dispatch time from the order, the grants and the operator's
/// template.
/// <para>
/// Nothing is rendered at enqueue and no body is stored. A retry eight days later re-reads
/// the grants and is therefore either correct or honestly refused, rather than sending a
/// link that expired while the message sat in the queue; nightly backups carry no working
/// links; and the remaining-downloads count is never stale.
/// </para>
/// </summary>
public sealed partial class EmailTemplateRenderer(
    NexusDbContext db,
    ISettingsReader settings,
    IDeliveryService delivery,
    IOptions<SiteOptions> site,
    ILogger<EmailTemplateRenderer> log,
    TimeProvider clock) : IEmailTemplateRenderer
{
    /// <summary>
    /// The landing page, never <c>/download/{token}</c> directly: SafeLinks and other mail
    /// scanners follow links, and a scanner must not be able to spend a download.
    /// </summary>
    public const string DownloadPathPrefix = "/download.html?t=";

    public async Task<(RenderedEmail? Email, string? Problem)> RenderAsync(
        OutboxEmail queued, CancellationToken ct = default)
    {
        if (queued.Kind is not (EmailKind.OrderConfirmation or EmailKind.DownloadLinks or EmailKind.RefundNotice))
            return (null, "unsupported_kind");

        if (queued.OrderId is not { } orderId)
            return (null, "no_order");

        var order = await db.Orders
            .Include(o => o.Items)
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);

        if (order is null) return (null, "order_not_found");

        /* Refunded and partially refunded still render: a refund notice is about an order
         * whose money moved back. A confirmation retried after a refund falls out at the
         * live-grant check below rather than claiming the links still work. */
        if (order.Status is OrderStatus.Pending or OrderStatus.Failed or OrderStatus.Cancelled)
            return (null, "order_not_paid");

        var language = Lang.Normalise(queued.Language);
        var model = await BuildModelAsync(order, language, ct);

        if (EmailTemplates.RequiresLiveGrants(queued.Kind) && model.Links.Count == 0)
            return (null, "no_live_grants");

        var (subjectKey, subjectDefault, bodyKey, bodyDefault) = EmailTemplates.For(queued.Kind, language);

        return (EmailComposer.Compose(
            queued.Kind,
            await settings.GetStringAsync(subjectKey, subjectDefault, ct),
            await settings.GetStringAsync(bodyKey, bodyDefault, ct),
            model,
            log), null);
    }

    private async Task<EmailModel> BuildModelAsync(Order order, string language, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        var storeName = await settings.GetStringAsync(SettingKeys.FromName, EmailTemplates.DefaultFromName, ct);
        var supportEmail = await settings.GetStringAsync(
            EmailSettingKeys.SupportAddress, EmailTemplates.DefaultSupportAddress, ct);

        var items = order.Items
            .OrderBy(i => i.Id)
            .Select(i => new EmailItem(Title(i, language), i.UnitPriceKwd))
            .ToList();

        var titles = order.Items.ToDictionary(i => i.BookId, i => Title(i, language));

        // One link per book: the newest sequence wins, because a re-issue supersedes the
        // grant it replaced and both rows stay in the table.
        var grants = await db.DownloadGrants
            .AsNoTracking()
            .Where(g => g.OrderId == order.Id && g.State == GrantState.Active
                        && g.ExpiresAt > now && g.UsedCount < g.MaxDownloads)
            .OrderByDescending(g => g.Sequence)
            .ToListAsync(ct);

        var baseUrl = site.Value.BaseOrFallback();
        var links = new List<EmailLink>();
        var seen = new HashSet<int>();

        foreach (var grant in grants)
        {
            if (!seen.Add(grant.BookId)) continue;

            var token = delivery.UnprotectToken(grant);
            if (string.IsNullOrEmpty(token))
            {
                // The key ring rotated, or the protected copy was never written. The grant
                // is still good; we simply cannot rebuild its link, and omitting it beats
                // emailing a URL with an empty token in it.
                LogTokenUnavailable(log, grant.Id, order.OrderNumber);
                continue;
            }

            links.Add(new EmailLink(
                titles.TryGetValue(grant.BookId, out var title) ? title : "",
                baseUrl + DownloadPathPrefix + Uri.EscapeDataString(token),
                grant.ExpiresAt,
                grant.MaxDownloads,
                Math.Max(0, grant.MaxDownloads - grant.UsedCount)));
        }

        links = [.. links.OrderBy(l => l.Title, StringComparer.Ordinal)];

        /* The prose describes the grants the reader actually holds, not today's setting: an
         * operator who shortens the window after a sale must not make last week's email
         * retroactively wrong. The settings are the fallback for an order with no links,
         * such as a refund notice. */
        var paidAt = order.PaidAt ?? order.CreatedAt;
        var expiryDays = links.Count > 0
            ? Math.Max(1, (int)Math.Ceiling((links.Min(l => l.ExpiresAt) - paidAt).TotalDays))
            : await settings.GetIntAsync(SettingKeys.LinkExpiryDays, 7, 1, 365, ct);
        var maxDownloads = links.Count > 0
            ? links.Max(l => l.MaxDownloads)
            : await settings.GetIntAsync(SettingKeys.MaxDownloads, 3, 1, 20, ct);

        return new EmailModel(
            StoreName: storeName,
            Language: language,
            OrderNumber: order.OrderNumber,
            OrderDate: paidAt,
            CustomerName: order.BuyerNameSnapshot,
            CustomerEmail: order.BuyerEmailSnapshot,
            TotalKwd: order.TotalKwd,
            RefundedKwd: order.RefundedTotalKwd,
            Items: items,
            Links: links,
            LinkExpiryDays: expiryDays,
            MaxDownloads: maxDownloads,
            LibraryUrl: baseUrl + "/library.html",
            SupportEmail: supportEmail,
            // C-withdrawn: fewer links than items means a title is being checked. The buyer
            // is told, rather than left to notice one book missing from a list of two.
            HasHeldItem: links.Count > 0 && links.Count < items.Count);
    }

    private static string Title(OrderItem item, string language) =>
        Lang.IsRtl(language) ? item.TitleArSnapshot : item.TitleEnSnapshot;

    [LoggerMessage(EventId = 5102, Level = LogLevel.Error,
        Message = "Grant {GrantId} on order {OrderNumber} has no recoverable token; its link was omitted.")]
    private static partial void LogTokenUnavailable(ILogger logger, int grantId, string orderNumber);
}
