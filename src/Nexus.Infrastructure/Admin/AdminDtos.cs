using Nexus.Application.Catalogue;

namespace Nexus.Infrastructure.Admin;

/// <summary>
/// Why an administrative action was refused. A stable code plus an English operator
/// message; the control panel owns the Arabic, because a wire protocol that carries
/// prose has two places to change a sentence.
/// </summary>
public sealed record AdminProblem(string Code, string? Detail = null);

/// <summary>
/// A book as the control panel sees it — the storefront's own shape, so <c>cover()</c>
/// and the card markup keep working, plus the facts only an operator needs.
/// </summary>
public sealed record AdminBookRow(
    int Id,
    string Cat,
    decimal Price,
    int Pages,
    int Year,
    int Sales,
    LocalisedText En,
    LocalisedText Ar,
    string? Motif,
    string? Isbn,
    string Languages,
    bool Published,
    bool HasPdf,
    bool PdfPlaceholder,
    long? PdfSizeBytes,
    int? PdfPageCount,
    string? PdfOriginalFileName,
    DateTimeOffset? PdfUploadedAt,
    string? PublishBlocker,
    DateTimeOffset AddedToStoreAt,
    DateTimeOffset UpdatedAt);

/// <summary>What the book form sends. Deliberately not the entity: no identifiers, no file facts.</summary>
public sealed record BookWrite(
    string CategorySlug,
    decimal PriceKwd,
    int Pages,
    int Year,
    string? Isbn,
    string TitleEn,
    string AuthorEn,
    string DescriptionEn,
    string TitleAr,
    string AuthorAr,
    string DescriptionAr,
    string? ContentLanguages,
    string? CoverMotif);

public sealed record AdminCategoryRow(
    string Slug,
    string NameEn,
    string NameAr,
    string Hue,
    int SortOrder,
    bool IsActive,
    int BookCount,
    int PublishedCount);

public sealed record CategoryWrite(string NameEn, string NameAr, string Hue, bool IsActive);

public sealed record AdminOrderRow(
    int Id,
    string OrderNumber,
    string BuyerName,
    string BuyerEmail,
    string Status,
    string Language,
    decimal TotalKwd,
    decimal RefundedKwd,
    int ItemCount,
    string? PaymentMethod,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PaidAt);

public sealed record AdminOrderItemRow(
    int BookId, string TitleEn, string TitleAr, string AuthorEn, string AuthorAr, decimal UnitPriceKwd);

public sealed record AdminOrderGrantRow(
    int GrantId, int BookId, string TitleEn, string TitleAr,
    int Sequence, int Used, int Max, string State, DateTimeOffset ExpiresAt);

public sealed record AdminOrderEmailRow(
    string PublicRef, string Kind, string Language, string To, string Status,
    int Attempts, DateTimeOffset CreatedAt, DateTimeOffset? SentAt, string? LastError);

public sealed record AdminOrderEventRow(string? From, string To, string Source, string? Detail, DateTimeOffset At);

public sealed record AdminOrderDetail(
    AdminOrderRow Order,
    int? CustomerId,
    string? StatusDetail,
    string? ReviewReason,
    decimal SubtotalKwd,
    decimal TotalCharged,
    string? TapChargeId,
    string? TapPaymentReference,
    string? TapGatewayReference,
    IReadOnlyList<AdminOrderItemRow> Items,
    IReadOnlyList<AdminOrderGrantRow> Grants,
    IReadOnlyList<AdminOrderEmailRow> Emails,
    IReadOnlyList<AdminOrderEventRow> Events);

public sealed record AdminCustomerRow(
    int Id,
    string Name,
    string Email,
    string Country,
    string Language,
    bool HasLogin,
    int OrderCount,
    int PaidOrderCount,
    decimal LifetimeValueKwd,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastOrderAt);

public sealed record AdminCustomerDetail(AdminCustomerRow Customer, IReadOnlyList<AdminOrderRow> Orders);

public sealed record AdminGrantRow(
    int GrantId,
    int OrderId,
    string OrderNumber,
    string BuyerEmail,
    int BookId,
    string TitleEn,
    string TitleAr,
    int Sequence,
    int Used,
    int Max,
    string State,
    string RevocationReason,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FlaggedAt,
    int DistinctIpCount,
    string? LastIpPrefix,
    DateTimeOffset? LastUsedAt);

public sealed record AdminDownloadLogRow(
    long Id, DateTimeOffset AtUtc, string? IpPrefix, string? UserAgent, string Outcome, long? BytesSent);

/// <summary>One day of the thirty-day trend. Always thirty rows, zeros included.</summary>
public sealed record DashboardDay(DateOnly Day, decimal RevenueKwd, int Orders, int Downloads);

public sealed record DashboardTopTitle(int BookId, string TitleEn, string TitleAr, int Copies, decimal RevenueKwd);

public sealed record DashboardSummary(
    decimal RevenueKwd30,
    decimal RefundedKwd30,
    int Orders30,
    int PaidOrders30,
    int Downloads30,
    int TitlesLive,
    int TitlesTotal,
    int PendingOrders,
    int CustomersTotal,
    IReadOnlyList<DashboardDay> Trend,
    IReadOnlyList<DashboardTopTitle> TopTitles,
    IReadOnlyList<AdminOrderRow> RecentOrders);

/// <summary>A paged slice. The catalogue needs no paging; orders, customers and logs do.</summary>
public sealed record Page<T>(int Total, int PageNumber, int PageSize, IReadOnlyList<T> Rows);
