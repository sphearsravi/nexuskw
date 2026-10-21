using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Nexus.Application.Common;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Settings;

/// <summary>
/// The publicly readable settings, resolved through the hardcoded allow-list.
/// <para>
/// Separate from <see cref="ISettingsReader"/> on purpose: this is the one path a
/// stranger can reach, and it must not be reachable by passing a key.
/// </para>
/// </summary>
public interface IPublicSettingsReader
{
    Task<IReadOnlyDictionary<string, string>> ReadPublicAsync(CancellationToken ct = default);
}

/// <summary>
/// Operator-editable settings over a 60-second cache, invalidated on save so a corrected
/// value takes effect at once.
/// <para>
/// <b>It clamps and never throws.</b> A store that stops selling because somebody typed
/// "seven" into a number field is a worse failure than the typo, so an unparseable or
/// out-of-range value is logged at Error and replaced by the documented fallback.
/// </para>
/// </summary>
public sealed class SettingsReader(
    NexusDbContext db,
    IMemoryCache cache,
    ILogger<SettingsReader> log) : ISettingsReader, IPublicSettingsReader
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private const string CachePrefix = "setting:";

    public async Task<string> GetStringAsync(string key, string fallback, CancellationToken ct = default)
    {
        var row = await RowAsync(key, ct);
        return string.IsNullOrWhiteSpace(row?.Value) ? fallback : row.Value;
    }

    public async Task<int> GetIntAsync(string key, int fallback, int min, int max, CancellationToken ct = default)
    {
        var row = await RowAsync(key, ct);
        var (value, how) = SettingsValue.ToInt(row?.Value, fallback, min, max);
        Report(key, row?.Value, how, value);
        return value;
    }

    public async Task<long> GetLongAsync(string key, long fallback, long min, long max, CancellationToken ct = default)
    {
        var row = await RowAsync(key, ct);
        var (value, how) = SettingsValue.ToLong(row?.Value, fallback, min, max);
        Report(key, row?.Value, how, value);
        return value;
    }

    public async Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken ct = default)
    {
        var row = await RowAsync(key, ct);
        var (value, how) = SettingsValue.ToBool(row?.Value, fallback);
        Report(key, row?.Value, how, value);
        return value;
    }

    public async Task SetAsync(
        string key, string value, bool isSecret, string? updatedBy, CancellationToken ct = default)
    {
        var existing = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        var now = DateTimeOffset.UtcNow;

        if (existing is null)
        {
            db.Settings.Add(new Setting
            {
                Key = key,
                Value = value,
                IsSecret = isSecret,
                UpdatedAt = now,
                UpdatedBy = updatedBy
            });
        }
        else
        {
            existing.Value = value;
            existing.IsSecret = isSecret;
            existing.UpdatedAt = now;
            existing.UpdatedBy = updatedBy;
        }

        await db.SaveChangesAsync(ct);
        cache.Remove(CachePrefix + key);
    }

    /// <summary>
    /// <b>The allow-list is the list in code, not a query.</b> The same table will hold
    /// the Tap secret key, and a projection over it would publish that the day somebody
    /// adds the row. A row marked secret is refused here as well — two locks on one door,
    /// because this is the door.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ReadPublicAsync(CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var key in SettingKeys.PubliclyReadable)
        {
            var row = await RowAsync(key, ct);
            if (row is { IsSecret: true })
            {
                log.LogError("Setting {Key} is on the public allow-list but marked secret; refusing it.", key);
                continue;
            }

            result[key] = key switch
            {
                SettingKeys.LinkExpiryDays =>
                    SettingsValue.ToInt(row?.Value, DeliveryDefaults.LinkExpiryDays, 1, 365).Value
                        .ToString(System.Globalization.CultureInfo.InvariantCulture),
                SettingKeys.MaxDownloads =>
                    SettingsValue.ToInt(row?.Value, DeliveryDefaults.MaxDownloads, 1, 20).Value
                        .ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => row?.Value ?? ""
            };
        }

        return result;
    }

    private async Task<Setting?> RowAsync(string key, CancellationToken ct)
    {
        if (cache.TryGetValue<Setting?>(CachePrefix + key, out var cached)) return cached;

        Setting? row;
        try
        {
            row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A settings table that cannot be read must not take the storefront down;
            // every caller has a fallback and this is what makes them use it.
            log.LogError(ex, "Could not read setting {Key}; falling back.", key);
            return null;
        }

        cache.Set(CachePrefix + key, row, Window);
        return row;
    }

    private void Report(string key, string? raw, SettingsParse how, object used)
    {
        if (how is SettingsParse.Ok or SettingsParse.Missing) return;

        log.LogError(
            "Setting {Key} has the value {Raw}, which is {Problem}. Using {Used} instead.",
            key, raw, how == SettingsParse.Unparseable ? "not a number" : "out of range", used);
    }
}

/// <summary>
/// The fallbacks, and the bounds they are clamped into. In one place so the reader, the
/// seeder and the public endpoint cannot drift apart.
/// </summary>
public static class DeliveryDefaults
{
    public const int LinkExpiryDays = 7;
    public const int LinkExpiryDaysMin = 1;
    public const int LinkExpiryDaysMax = 365;

    public const int MaxDownloads = 3;
    public const int MaxDownloadsMin = 1;
    public const int MaxDownloadsMax = 20;

    public const bool WatermarkEnabled = true;

    public const int DistinctIpThreshold = 4;
    public const int DistinctIpThresholdMin = 2;
    public const int DistinctIpThresholdMax = 50;

    public const int ReissueLimitPerItem = 5;
    public const int ReissueLimitPerItemMin = 1;
    public const int ReissueLimitPerItemMax = 20;

    public const long MaxUploadBytes = 67_108_864;
    public const long StampCacheBudgetBytes = 2_147_483_648;
}
