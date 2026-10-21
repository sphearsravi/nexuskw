using Nexus.Domain;

namespace Nexus.Infrastructure.Email;

/// <summary>One purchased line, as it was sold. Never recalculated.</summary>
public sealed record EmailItem(string Title, decimal UnitPriceKwd);

/// <summary>One live download right, resolved at send time and not a moment earlier.</summary>
public sealed record EmailLink(
    string Title, string Url, DateTimeOffset ExpiresAt, int MaxDownloads, int Remaining);

/// <summary>
/// Everything the templates may reference, resolved before rendering — no lazy loading
/// inside a renderer that runs on a background thread.
/// <para>
/// Money here is the snapshot the order captured. An email that disagreed with the invoice
/// would be worse than no email: a receipt is a record of what was charged, not a fresh
/// calculation.
/// </para>
/// </summary>
public sealed record EmailModel(
    string StoreName,
    string Language,
    string OrderNumber,
    DateTimeOffset OrderDate,
    string CustomerName,
    string CustomerEmail,
    decimal TotalKwd,
    decimal RefundedKwd,
    IReadOnlyList<EmailItem> Items,
    IReadOnlyList<EmailLink> Links,
    int LinkExpiryDays,
    int MaxDownloads,
    string LibraryUrl,
    string SupportEmail,
    bool HasHeldItem)
{
    public bool IsRtl => Lang.IsRtl(Language);

    public string Money(decimal amount) => MoneyText.Format(amount, Language);

    public string Date(DateTimeOffset at) => KuwaitDates.Format(at, Language);

    /// <summary>
    /// Currency.Format, not <see cref="Money"/>: this is the bare three-decimal amount used
    /// where the currency word would be noise.
    /// </summary>
    public string Bare(decimal amount) => Currency.Kwd.Format(amount);
}
