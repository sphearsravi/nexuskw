using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Nexus.Application.Common;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Settings;

/// <summary>
/// Writes the <c>delivery.*</c> defaults if they are not there yet.
/// <para>
/// A runtime seeder rather than <c>HasData</c>: migration seed data is rewritten by the
/// next migration that touches the table, which would quietly undo an operator's edits.
/// A key that already exists is left exactly as the operator left it.
/// </para>
/// </summary>
public sealed class DeliverySettingsSeeder(NexusDbContext db)
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        var defaults = new (string Key, string Value)[]
        {
            (SettingKeys.LinkExpiryDays, Text(DeliveryDefaults.LinkExpiryDays)),
            (SettingKeys.MaxDownloads, Text(DeliveryDefaults.MaxDownloads)),
            (SettingKeys.WatermarkEnabled, DeliveryDefaults.WatermarkEnabled ? "true" : "false"),
            (SettingKeys.DistinctIpThreshold, Text(DeliveryDefaults.DistinctIpThreshold)),
            (SettingKeys.MaxUploadBytes, Text(DeliveryDefaults.MaxUploadBytes)),
            (SettingKeys.ReissueLimitPerItem, Text(DeliveryDefaults.ReissueLimitPerItem)),
            (SettingKeys.StampCacheBudgetBytes, Text(DeliveryDefaults.StampCacheBudgetBytes))
        };

        var keys = defaults.Select(d => d.Key).ToArray();
        var present = await db.Settings
            .Where(s => keys.Contains(s.Key))
            .Select(s => s.Key)
            .ToListAsync(ct);

        var added = false;
        foreach (var (key, value) in defaults)
        {
            if (present.Contains(key)) continue;

            db.Settings.Add(new Setting
            {
                Key = key,
                Value = value,
                IsSecret = false,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = "seed"
            });
            added = true;
        }

        if (added) await db.SaveChangesAsync(ct);
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}
