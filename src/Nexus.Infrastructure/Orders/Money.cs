using Nexus.Domain;

namespace Nexus.Infrastructure.Orders;

/// <summary>
/// An amount paired with the <see cref="Currency"/> that owns its scale, so a figure
/// cannot drift away from the thing that knows KWD has three decimals.
/// <para>
/// Deliberately absent: multiplication, subtraction, and any implicit conversion to
/// <see cref="decimal"/>. There is no quantity in this model (ST-5), so there is no
/// caller for multiplication, and forcing <c>.Amount</c> at the persistence boundary
/// keeps every unwrap visible in a diff.
/// </para>
/// <para>
/// <b>Not persisted.</b> Order columns are plain <c>decimal</c> with an explicit
/// <c>decimal(18,3)</c>; an owned type across five money columns would buy nothing and
/// make the one migration people actually read harder to review.
/// </para>
/// </summary>
public readonly record struct Money(decimal Amount, Currency Currency)
{
    public static Money Kwd(decimal amount) => new(amount, Currency.Kwd);

    public static Money Zero(Currency currency) => new(0m, currency);

    /// <summary>Rounded to the currency's scale, away from zero — a buyer is never charged a fraction of a fils.</summary>
    public Money Rounded => this with { Amount = Currency.Round(Amount) };

    public static Money operator +(Money left, Money right)
    {
        // Mixing currencies is a bug, never an implicit conversion: there is no FX
        // step in v1 and inventing one here would hide the day somebody adds AED.
        if (left.Currency != right.Currency)
        {
            throw new InvalidOperationException(
                $"Cannot add {left.Currency.Code} to {right.Currency.Code}.");
        }

        return left with { Amount = left.Amount + right.Amount };
    }

    /// <summary>
    /// Sums without rounding. Rounding happens exactly twice in a quote — once per line
    /// and once on the total — never on every addition, because that is how a sum drifts.
    /// </summary>
    public static Money Sum(IEnumerable<Money> amounts, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(amounts);

        var total = Zero(currency);
        foreach (var amount in amounts) total += amount;
        return total;
    }

    /// <summary>Invariant culture, Western digits, exactly the currency's scale: <c>"6.500"</c>.</summary>
    public override string ToString() => Currency.Format(Amount);
}
