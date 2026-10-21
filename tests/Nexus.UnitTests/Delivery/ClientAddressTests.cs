using Nexus.Infrastructure.Delivery;

namespace Nexus.UnitTests.Delivery;

/// <summary>
/// Sharing is counted on a prefix, not an address: an IPv6 client with privacy
/// extensions rotates inside its own /64 between requests and would otherwise flag
/// itself on the second download.
/// </summary>
public sealed class ClientAddressTests
{
    [Theory]
    [InlineData("41.238.1.9", "41.238.1.9/32")]
    [InlineData("::ffff:41.238.1.9", "41.238.1.9/32")]
    [InlineData("2001:db8:1234:5678:abcd:ef01:2345:6789", "2001:db8:1234:5678::/64")]
    [InlineData("2001:db8:1234:5678:0:0:0:1", "2001:db8:1234:5678::/64")]
    public void An_address_folds_to_its_prefix(string address, string expected) =>
        Assert.Equal(expected, ClientAddress.Prefix(address));

    [Fact]
    public void Two_addresses_in_one_slash_64_are_the_same_household() =>
        Assert.Equal(
            ClientAddress.Prefix("2001:db8:1:2:3:4:5:6"),
            ClientAddress.Prefix("2001:db8:1:2:dead:beef:0:1"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-address")]
    public void An_unresolvable_address_is_null_rather_than_a_guess(string? address) =>
        Assert.Null(ClientAddress.Prefix(address));
}
