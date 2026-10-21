namespace Nexus.Infrastructure.Orders;

/// <summary>Which unique index a duplicate-key failure came from.</summary>
public enum UniqueViolation
{
    /// <summary>Not a constraint this code knows how to recover from.</summary>
    Unknown = 0,

    /// <summary>Two requests carrying the same <c>Idempotency-Key</c> raced.</summary>
    OrderIdempotencyKey = 1,

    /// <summary>Two order numbers collided — a 1-in-10^9 draw, or a test forcing one.</summary>
    OrderNumber = 2,

    /// <summary>Two first-time buyers with the same email raced to create the customer row.</summary>
    CustomerNormalisedEmail = 3
}

/// <summary>
/// Reads a duplicate-key failure back to the index that caused it.
/// <para>
/// Branching on the constraint is the whole of B2: the three indexes want three
/// different recoveries, and "retry the save" is wrong for all of them. The match is on
/// the index name inside the server's message, which SQL Server quotes verbatim
/// whatever language the rest of the sentence is in.
/// </para>
/// </summary>
public static class OrderConstraints
{
    public const string OrderIdempotencyKeyIndex = "IX_Orders_IdempotencyKey";
    public const string OrderNumberIndex = "IX_Orders_OrderNumber";
    public const string CustomerNormalisedEmailIndex = "IX_Customers_NormalisedEmail";

    /// <summary>SQL Server: 2601 duplicate key in a unique index, 2627 unique constraint violation.</summary>
    public static bool IsDuplicateKey(int sqlErrorNumber) => sqlErrorNumber is 2601 or 2627;

    public static UniqueViolation Classify(string? message)
    {
        if (string.IsNullOrEmpty(message)) return UniqueViolation.Unknown;

        if (Mentions(message, OrderIdempotencyKeyIndex)) return UniqueViolation.OrderIdempotencyKey;
        if (Mentions(message, OrderNumberIndex)) return UniqueViolation.OrderNumber;
        if (Mentions(message, CustomerNormalisedEmailIndex)) return UniqueViolation.CustomerNormalisedEmail;

        return UniqueViolation.Unknown;
    }

    private static bool Mentions(string message, string indexName) =>
        message.Contains(indexName, StringComparison.OrdinalIgnoreCase);
}
