using Nexus.Infrastructure.Orders;

namespace Nexus.UnitTests.Orders;

/// <summary>
/// The three unique indexes want three different recoveries (B2), so reading the index
/// name back out of the server's message is load-bearing. These are the messages SQL
/// Server 2017 actually returns.
/// </summary>
public sealed class OrdersConstraintTests
{
    private const string Duplicate =
        "Cannot insert duplicate key row in object 'dbo.{0}' with unique index '{1}'. " +
        "The duplicate key value is ({2}).";

    private static string Message(string table, string index, string value) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, Duplicate, table, index, value);

    [Fact]
    public void An_idempotency_key_clash_is_recognised() =>
        Assert.Equal(UniqueViolation.OrderIdempotencyKey, OrderConstraints.Classify(
            Message("Orders", "IX_Orders_IdempotencyKey", "0f0c...")));

    [Fact]
    public void An_order_number_clash_is_recognised() =>
        Assert.Equal(UniqueViolation.OrderNumber, OrderConstraints.Classify(
            Message("Orders", "IX_Orders_OrderNumber", "KW-261021-7F3K2M")));

    [Fact]
    public void A_customer_email_clash_is_recognised() =>
        Assert.Equal(UniqueViolation.CustomerNormalisedEmail, OrderConstraints.Classify(
            Message("Customers", "IX_Customers_NormalisedEmail", "FATIMA.A@EXAMPLE.COM")));

    // A clash we have no recovery for must surface, not be retried into the same wall.
    [Fact]
    public void An_unrelated_index_is_not_guessed_at() =>
        Assert.Equal(UniqueViolation.Unknown, OrderConstraints.Classify(
            Message("OrderItems", "IX_OrderItems_OrderId_BookId", "4, 11")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A network-related or instance-specific error occurred.")]
    public void Anything_else_is_unknown(string? message) =>
        Assert.Equal(UniqueViolation.Unknown, OrderConstraints.Classify(message));

    [Theory]
    [InlineData(2601, true)]
    [InlineData(2627, true)]
    [InlineData(547, false)]   // FK violation
    [InlineData(1205, false)]  // deadlock victim
    public void Only_duplicate_key_numbers_route_here(int number, bool expected) =>
        Assert.Equal(expected, OrderConstraints.IsDuplicateKey(number));
}
