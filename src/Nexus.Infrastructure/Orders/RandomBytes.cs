using System.Security.Cryptography;

namespace Nexus.Infrastructure.Orders;

/// <summary>
/// A source of random bytes. An interface only so a test can seed it: production has
/// exactly one implementation and it is the CSPRNG.
/// </summary>
public interface IRandomBytes
{
    void Fill(Span<byte> destination);
}

/// <summary>The only implementation that ships. An order number must be unguessable.</summary>
public sealed class CryptoRandomBytes : IRandomBytes
{
    public void Fill(Span<byte> destination) => RandomNumberGenerator.Fill(destination);
}
