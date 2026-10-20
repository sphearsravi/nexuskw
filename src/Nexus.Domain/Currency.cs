using System.Globalization;

namespace Nexus.Domain;

/// <summary>
/// A supported currency and, critically, its decimal scale.
/// <para>
/// KWD is a three-decimal currency. Every amount that reaches Tap — the charge
/// amount and the value hashed into the webhook signature — must be formatted at
/// the currency's own scale, or the HMAC will not match and no order will ever
/// complete. See PLAN.md §6.3.
/// </para>
/// </summary>
public sealed record Currency
{
    public static readonly Currency Kwd = new("KWD", 3);
    public static readonly Currency Aed = new("AED", 2);

    public static readonly IReadOnlyList<Currency> All = [Kwd, Aed];

    private Currency(string code, int scale)
    {
        Code = code;
        Scale = scale;
    }

    /// <summary>ISO 4217 code, e.g. "KWD".</summary>
    public string Code { get; }

    /// <summary>Number of decimal places this currency is quoted in.</summary>
    public int Scale { get; }

    public static Currency FromCode(string code) =>
        All.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentOutOfRangeException(nameof(code), code, "Unsupported currency.");

    public static bool TryFromCode(string? code, out Currency currency)
    {
        currency = All.FirstOrDefault(c =>
            string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase))!;
        return currency is not null;
    }

    /// <summary>
    /// Rounds to this currency's scale. Away-from-zero, the retail convention:
    /// a customer is never charged a fraction of a fils.
    /// </summary>
    public decimal Round(decimal amount) =>
        Math.Round(amount, Scale, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Rounds then renders with exactly <see cref="Scale"/> decimals, invariant
    /// culture. This is the representation sent to Tap and hashed; it must never
    /// pick up a thousands separator or Arabic-Indic digits from the UI culture.
    /// </summary>
    public string Format(decimal amount) =>
        Round(amount).ToString("F" + Scale.ToString(CultureInfo.InvariantCulture),
                               CultureInfo.InvariantCulture);

    public override string ToString() => Code;
}
