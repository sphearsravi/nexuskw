using System.Net;
using System.Net.Sockets;

namespace Nexus.Infrastructure.Delivery;

/// <summary>
/// Turns a client address into the prefix that sharing is counted on.
/// <para>
/// A prefix, not an address: an IPv6 client with privacy extensions rotates inside its
/// own <c>/64</c> between requests and would otherwise flag itself on the second
/// download. It is also less than is needed to track a person, which is the point.
/// </para>
/// </summary>
public static class ClientAddress
{
    public static string? Prefix(IPAddress? address)
    {
        if (address is null) return null;

        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
            return address.ToString() + "/32";

        if (address.AddressFamily != AddressFamily.InterNetworkV6) return null;

        var bytes = address.GetAddressBytes();
        for (var i = 8; i < bytes.Length; i++) bytes[i] = 0;
        return new IPAddress(bytes).ToString() + "/64";
    }

    public static string? Prefix(string? address) =>
        IPAddress.TryParse(address, out var parsed) ? Prefix(parsed) : null;
}
