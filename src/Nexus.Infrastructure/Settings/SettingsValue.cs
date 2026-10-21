using System.Globalization;

namespace Nexus.Infrastructure.Settings;

/// <summary>What a stored string turned into, and whether it had to be corrected.</summary>
public enum SettingsParse
{
    Ok = 0,
    Missing = 1,
    Unparseable = 2,
    Clamped = 3
}

/// <summary>
/// The parsing half of <c>ISettingsReader</c>, pulled out as pure functions so the rule
/// that matters — <b>clamp, never throw</b> — is tested exhaustively without a database.
/// </summary>
public static class SettingsValue
{
    public static (long Value, SettingsParse How) ToLong(string? raw, long fallback, long min, long max)
    {
        if (min > max) (min, max) = (max, min);
        var safeFallback = Math.Clamp(fallback, min, max);

        if (string.IsNullOrWhiteSpace(raw)) return (safeFallback, SettingsParse.Missing);

        if (!long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return (safeFallback, SettingsParse.Unparseable);

        var clamped = Math.Clamp(parsed, min, max);
        return (clamped, clamped == parsed ? SettingsParse.Ok : SettingsParse.Clamped);
    }

    public static (int Value, SettingsParse How) ToInt(string? raw, int fallback, int min, int max)
    {
        var (value, how) = ToLong(raw, fallback, min, max);
        return ((int)value, how);
    }

    /// <summary>
    /// Deliberately generous about what counts as a yes or a no. The alternative is an
    /// operator typing "yes" and silently turning the watermark off.
    /// </summary>
    public static (bool Value, SettingsParse How) ToBool(string? raw, bool fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (fallback, SettingsParse.Missing);

        return raw.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => (true, SettingsParse.Ok),
            "false" or "0" or "no" or "off" => (false, SettingsParse.Ok),
            _ => (fallback, SettingsParse.Unparseable)
        };
    }
}
