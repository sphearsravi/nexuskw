using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Nexus.Application.Common;
using Nexus.Infrastructure.Persistence;
using Nexus.Infrastructure.Settings;

namespace Nexus.Infrastructure.Admin;

public enum SettingKind
{
    Text = 0,
    MultilineText = 1,
    Integer = 2,
    Boolean = 3
}

/// <summary>One editable key: what it is, and what a legal value looks like.</summary>
public sealed record SettingSpec(
    string Key, SettingKind Kind, string Group, long? Min = null, long? Max = null);

/// <summary>
/// A settings row as the panel sees it.
/// <para>
/// <see cref="Value"/> is <c>null</c> whenever <see cref="IsSecret"/> is true — not
/// shortened, not starred server-side, absent. Masking in the browser would mean the
/// secret was still on the wire and still in the operator's browser cache.
/// </para>
/// </summary>
public sealed record AdminSettingRow(
    string Key,
    string? Value,
    bool IsSecret,
    bool IsSet,
    bool Editable,
    string Kind,
    string Group,
    long? Min,
    long? Max,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy);

/// <summary>
/// The operator-editable settings.
/// <para>
/// The editable set is an <b>allow-list in code</b>, exactly like the public reader's.
/// The same table will hold the Tap keys, and a settings screen that writes whatever key
/// it is handed is a way to set one from a browser.
/// </para>
/// </summary>
public sealed class AdminSettingsService(
    NexusDbContext db,
    ISettingsReader settings,
    IAuditLog audit,
    IAdminActorAccessor actors)
{
    public const int MaxValueLength = 4000;

    /// <summary>
    /// Delivery and email only. No gateway key and no SMTP credential: those are server
    /// environment configuration, and a store whose payment secret can be changed from a
    /// web form has moved its most valuable credential into its least trusted surface.
    /// </summary>
    public static readonly IReadOnlyList<SettingSpec> Editable =
    [
        new(SettingKeys.LinkExpiryDays, SettingKind.Integer, "delivery",
            DeliveryDefaults.LinkExpiryDaysMin, DeliveryDefaults.LinkExpiryDaysMax),
        new(SettingKeys.MaxDownloads, SettingKind.Integer, "delivery",
            DeliveryDefaults.MaxDownloadsMin, DeliveryDefaults.MaxDownloadsMax),
        new(SettingKeys.WatermarkEnabled, SettingKind.Boolean, "delivery"),
        new(SettingKeys.DistinctIpThreshold, SettingKind.Integer, "delivery",
            DeliveryDefaults.DistinctIpThresholdMin, DeliveryDefaults.DistinctIpThresholdMax),
        new(SettingKeys.ReissueLimitPerItem, SettingKind.Integer, "delivery",
            DeliveryDefaults.ReissueLimitPerItemMin, DeliveryDefaults.ReissueLimitPerItemMax),
        new(SettingKeys.MaxUploadBytes, SettingKind.Integer, "delivery", 1_048_576, 268_435_456),

        new(SettingKeys.FromAddress, SettingKind.Text, "email"),
        new(SettingKeys.FromName, SettingKind.Text, "email"),
        new(SettingKeys.ConfirmationSubjectEn, SettingKind.Text, "email"),
        new(SettingKeys.ConfirmationSubjectAr, SettingKind.Text, "email"),
        new(SettingKeys.ConfirmationBodyEn, SettingKind.MultilineText, "email"),
        new(SettingKeys.ConfirmationBodyAr, SettingKind.MultilineText, "email"),
        new(SettingKeys.ResendLimitPerDay, SettingKind.Integer, "email", 1, 100)
    ];

    private static readonly Dictionary<string, SettingSpec> BySpec =
        Editable.ToDictionary(s => s.Key, StringComparer.Ordinal);

    /// <summary>
    /// Every row in the table, so the operator can see what exists, with the editable ones
    /// flagged and the secret ones emptied.
    /// </summary>
    public async Task<IReadOnlyList<AdminSettingRow>> ListAsync(CancellationToken ct = default)
    {
        var stored = await db.Settings.AsNoTracking().ToListAsync(ct);
        var rows = new List<AdminSettingRow>();

        foreach (var spec in Editable)
        {
            var row = stored.FirstOrDefault(s => s.Key == spec.Key);
            rows.Add(new AdminSettingRow(
                spec.Key,
                row is { IsSecret: true } ? null : row?.Value,
                row?.IsSecret ?? false,
                row is not null,
                Editable: true,
                spec.Kind.ToString(),
                spec.Group,
                spec.Min,
                spec.Max,
                row?.UpdatedAt,
                row?.UpdatedBy));
        }

        // Anything else the table holds is shown but not editable here — and if it is
        // marked secret its value never leaves the server.
        foreach (var row in stored.Where(s => !BySpec.ContainsKey(s.Key)).OrderBy(s => s.Key))
        {
            rows.Add(new AdminSettingRow(
                row.Key,
                row.IsSecret ? null : row.Value,
                row.IsSecret,
                IsSet: true,
                Editable: false,
                SettingKind.Text.ToString(),
                row.Key.Split('.')[0],
                null, null,
                row.UpdatedAt,
                row.UpdatedBy));
        }

        return rows;
    }

    /// <summary>
    /// Writes one key through <see cref="ISettingsReader.SetAsync"/>, which invalidates the
    /// cache so a corrected value takes effect at once.
    /// </summary>
    public async Task<(AdminSettingRow? Row, IReadOnlyList<FieldError> Errors)> SetAsync(
        string key, string? value, CancellationToken ct = default)
    {
        if (!BySpec.TryGetValue(key, out var spec))
            return (null, [new FieldError(0, "key", "not_editable", key)]);

        var errors = Check(spec, value);
        if (errors.Count > 0) return (null, errors);

        var existing = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        var normalised = Normalise(spec, value!);

        // A secret stays a secret: the flag is carried forward from the stored row, never
        // taken from the request, so nothing a browser sends can unmask one.
        var isSecret = existing?.IsSecret ?? false;

        /* The audit row is added to the same scoped context the settings reader writes
         * through, and nothing is saved here: the reader's own SaveChanges commits the
         * value and the trail together. Two saves would let a trail survive a change that
         * rolled back, which is a worse lie than no trail at all. */
        await audit.RecordAsync("Setting", key, "setting.update",
            new { Value = isSecret ? "(secret)" : existing?.Value },
            new { Value = isSecret ? "(secret)" : normalised }, ct);

        await settings.SetAsync(key, normalised, isSecret, actors.Current.Email ?? actors.Current.Id, ct);

        return ((await ListAsync(ct)).FirstOrDefault(r => r.Key == key), []);
    }

    private static List<FieldError> Check(SettingSpec spec, string? value)
    {
        var errors = new List<FieldError>();

        if (value is null)
        {
            errors.Add(new FieldError(0, spec.Key, "required"));
            return errors;
        }

        if (value.Length > MaxValueLength)
        {
            errors.Add(new FieldError(0, spec.Key, "too_long"));
            return errors;
        }

        switch (spec.Kind)
        {
            case SettingKind.Integer:
                if (!long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    errors.Add(new FieldError(0, spec.Key, "not_a_number", value));
                else if (n < (spec.Min ?? long.MinValue) || n > (spec.Max ?? long.MaxValue))
                    errors.Add(new FieldError(0, spec.Key, "out_of_range",
                        $"{spec.Min}..{spec.Max}"));
                break;

            case SettingKind.Boolean:
                if (!bool.TryParse(value.Trim(), out _))
                    errors.Add(new FieldError(0, spec.Key, "not_a_boolean", value));
                break;

            case SettingKind.Text:
            case SettingKind.MultilineText:
            default:
                if (string.IsNullOrWhiteSpace(value)) errors.Add(new FieldError(0, spec.Key, "required"));
                break;
        }

        return errors;
    }

    /// <summary>
    /// Stored in the shape the reader parses: lowercase <c>true</c>/<c>false</c>, plain
    /// digits. "True" and " 7 " both read back correctly today, but only because the
    /// reader is forgiving, and that is not a property to depend on.
    /// </summary>
    private static string Normalise(SettingSpec spec, string value) => spec.Kind switch
    {
        SettingKind.Integer => long.Parse(value.Trim(), CultureInfo.InvariantCulture)
            .ToString(CultureInfo.InvariantCulture),
        SettingKind.Boolean => bool.Parse(value.Trim()) ? "true" : "false",
        SettingKind.Text => value.Trim(),
        _ => value
    };
}
