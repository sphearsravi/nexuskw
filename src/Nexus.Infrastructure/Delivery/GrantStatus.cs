namespace Nexus.Infrastructure.Delivery;

/// <summary>
/// What the landing page and the library are told about a link.
/// <para>
/// <b>The buyer's name and email are deliberately absent.</b> The token may have been
/// forwarded, and the person holding it is not necessarily the person who paid.
/// </para>
/// </summary>
public sealed record GrantStatusDto(
    int BookId,
    string TitleEn,
    string TitleAr,
    string OrderNumber,
    int Used,
    int Max,
    int Left,
    DateTimeOffset ExpiresAt,
    string State,
    long? SizeBytes,
    string DownloadUrl);

/// <summary>
/// The read-only half of the download path: it answers questions and counts nothing.
/// Separate from <c>IDeliveryService</c> because the frozen contract has no read method
/// and a status call that could consume an allowance would be a trap.
/// </summary>
public interface IGrantStatusReader
{
    Task<GrantStatusDto?> DescribeAsync(string token, CancellationToken ct = default);
}
