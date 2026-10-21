using Nexus.Application.Orders;
using Nexus.Domain;

namespace Nexus.Infrastructure.Orders;

/// <summary>
/// The arithmetic half of pricing: lines that have already been read from the catalogue
/// in, a priced basket out. No clock, no randomness, no I/O, no <c>DbContext</c> — which
/// is what makes the three-decimal tests cheap to write and impossible to bypass.
/// </summary>
public static class BasketPricing
{
    /// <summary>Kuwait levies no VAT, and v1 sells only in KWD. These are the only two places the constants live.</summary>
    public const decimal VatRate = 0m;

    public const decimal FxRate = 1m;

    /// <summary>
    /// Prices the lines. Throws on an empty set or a non-positive price rather than
    /// returning a zero-amount basket: a KWD 0.000 charge is not a thing this product
    /// sells (D7), so the engine refuses to produce one and no caller can.
    /// </summary>
    public static PricedBasket Quote(IReadOnlyList<PricedLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (lines.Count == 0)
        {
            throw new ArgumentException("A basket with no lines has no price.", nameof(lines));
        }

        var kwd = Currency.Kwd;

        // Rounded per line first. Catalogue prices are already stored at three decimals
        // so this is a no-op today; it stays so a future discount or proration cannot
        // introduce a fourth decimal unnoticed.
        var rounded = new List<Money>(lines.Count);
        foreach (var line in lines)
        {
            if (line.UnitPriceKwd <= 0m)
            {
                throw new ArgumentException(
                    $"Book {line.BookId} is priced at {kwd.Format(line.UnitPriceKwd)}; there are no free books.",
                    nameof(lines));
            }

            rounded.Add(Money.Kwd(line.UnitPriceKwd).Rounded);
        }

        var subtotal = Money.Sum(rounded, kwd).Rounded;

        // No VAT step and no conversion step — not a step that multiplies by one and
        // adds zero, but no step at all. A UAE launch edits this method and nothing else.
        var vatAmount = Money.Zero(kwd);
        var total = (subtotal + vatAmount).Rounded;

        return new PricedBasket(lines, subtotal.Amount, VatRate, vatAmount.Amount, total.Amount);
    }

    /// <summary>
    /// The total in integer fils, for comparison against the browser's advisory figure.
    /// Integer because comparing a JavaScript float accumulation to a server decimal
    /// produced spurious mismatches on a quarter of multi-item carts (C-fils).
    /// </summary>
    public static int ToFils(decimal totalKwd) => (int)(Currency.Kwd.Round(totalKwd) * 1000m);
}
