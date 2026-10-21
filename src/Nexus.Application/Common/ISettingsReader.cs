namespace Nexus.Application.Common;

/// <summary>
/// Operator-editable settings, cached briefly and invalidated on save so a corrected
/// template takes effect at once.
/// <para>
/// <b>It clamps and never throws.</b> A settings table is edited by a human through a web
/// form; an unparseable or out-of-range value must degrade to the documented fallback and
/// log at Error, not take the storefront down. A store that stops selling because someone
/// typed "seven" in a number field is a worse failure than the typo.
/// </para>
/// </summary>
public interface ISettingsReader
{
    Task<string> GetStringAsync(string key, string fallback, CancellationToken ct = default);

    /// <summary>Clamped into [<paramref name="min"/>, <paramref name="max"/>].</summary>
    Task<int> GetIntAsync(string key, int fallback, int min, int max, CancellationToken ct = default);

    Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken ct = default);

    Task<long> GetLongAsync(string key, long fallback, long min, long max, CancellationToken ct = default);

    /// <summary>Writes and invalidates the cache. <paramref name="isSecret"/> masks it in the admin.</summary>
    Task SetAsync(string key, string value, bool isSecret, string? updatedBy, CancellationToken ct = default);
}

/// <summary>
/// Settings keys owned by phases 2–5. Named constants rather than loose strings, so a
/// typo is a compile error instead of a silent fallback.
/// </summary>
public static class SettingKeys
{
    public const string LinkExpiryDays = "delivery.linkExpiryDays";
    public const string MaxDownloads = "delivery.maxDownloads";
    public const string WatermarkEnabled = "delivery.watermarkEnabled";
    public const string DistinctIpThreshold = "delivery.distinctIpThreshold";
    public const string MaxUploadBytes = "delivery.maxUploadBytes";
    public const string ReissueLimitPerItem = "delivery.reissueLimitPerItem";
    public const string StampCacheBudgetBytes = "delivery.stampCacheBudgetBytes";

    public const string FromAddress = "email.fromAddress";
    public const string FromName = "email.fromName";
    public const string ConfirmationSubjectEn = "email.confirmation.subject.en";
    public const string ConfirmationSubjectAr = "email.confirmation.subject.ar";
    public const string ConfirmationBodyEn = "email.confirmation.body.en";
    public const string ConfirmationBodyAr = "email.confirmation.body.ar";
    public const string ResendLimitPerDay = "email.resend.limitPerDay";

    /// <summary>
    /// The only keys <c>/api/settings/public</c> may ever return. A hardcoded allow-list,
    /// never a projection over the table — the same table will hold the Tap secret key,
    /// and a projection would leak it the day somebody adds a row.
    /// </summary>
    public static readonly string[] PubliclyReadable = [LinkExpiryDays, MaxDownloads];
}

/// <summary>Who changed what. Every administrative write goes through here.</summary>
public interface IAuditLog
{
    Task RecordAsync(
        string entity, string entityId, string action,
        object? before, object? after,
        string? actorId, string? actorEmail, string? ipAddress,
        CancellationToken ct = default);
}
