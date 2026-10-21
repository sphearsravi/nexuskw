namespace Nexus.Infrastructure.Accounts;

/// <summary>
/// One book a signed-in customer owns.
/// <para>
/// Both titles are sent and the page picks one, exactly as the catalogue client does:
/// switching language must not need a round trip, and an Arabic reader must not see the
/// English title because that is the one the snapshot happened to be read in.
/// </para>
/// </summary>
/// <param name="DownloadUrl">
/// Null when the grant can no longer serve. A dead link that still looks like a button is
/// the complaint AC-4 exists to remove.
/// </param>
public sealed record LibraryItem(
    int GrantId,
    int BookId,
    string TitleEn,
    string TitleAr,
    string AuthorEn,
    string AuthorAr,
    string OrderNumber,
    DateTimeOffset PurchasedAt,
    int Used,
    int Max,
    int Left,
    DateTimeOffset ExpiresAt,
    string State,
    string? DownloadUrl,
    bool CanReIssue);

public sealed record OrderSummary(
    string OrderNumber,
    DateTimeOffset PlacedAt,
    DateTimeOffset? PaidAt,
    string Status,
    string Currency,
    decimal TotalKwd,
    int ItemCount);

public sealed record InvoiceLine(
    string TitleEn, string TitleAr, string AuthorEn, string AuthorAr, decimal UnitPriceKwd);

/// <summary>
/// AC-5. Every money field is the snapshot the order captured, never a recalculation: an
/// invoice is a record of what was charged, and a price edit two years later must not
/// rewrite it.
/// </summary>
/// <param name="VatRate">Always zero in v1 — Kuwait levies none. Carried so the page can
/// state it rather than leave a reader wondering whether it was forgotten (PM-10).</param>
public sealed record Invoice(
    string OrderNumber,
    DateTimeOffset PlacedAt,
    DateTimeOffset? PaidAt,
    string Status,
    string BuyerName,
    string BuyerEmail,
    string Language,
    string Currency,
    decimal SubtotalKwd,
    decimal VatRate,
    decimal VatAmount,
    decimal TotalKwd,
    decimal RefundedKwd,
    IReadOnlyList<InvoiceLine> Lines);
