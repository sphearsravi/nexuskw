using Nexus.Application.Orders;

namespace Nexus.Infrastructure.Orders;

/// <summary>
/// Generates <c>KW-{yyMMdd}-{6 Crockford base32}</c>, 16 characters.
/// <para>
/// The number is read out over the phone, printed on a watermark and used as Tap's
/// transaction reference, so it is short, unambiguous aloud, and not a secret: nothing
/// anywhere accepts it as proof of anything. It is also non-sequential, because a
/// public-source store with a guessable order number hands a competitor its volume.
/// </para>
/// </summary>
public sealed class OrderNumberGenerator(IRandomBytes random) : IOrderNumberGenerator
{
    /// <summary>
    /// Crockford base32: no <c>I</c>, <c>L</c> or <c>O</c> (misread as 1 and 0 over the
    /// phone — the number's main job) and no <c>U</c> (accidental profanity).
    /// </summary>
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public const int RandomLength = 6;

    /// <summary>Kuwait is UTC+3 all year — no DST, so a fixed offset is correct, not an approximation.</summary>
    public static readonly TimeSpan KuwaitOffset = TimeSpan.FromHours(3);

    public string Next(DateTimeOffset now)
    {
        // The date part is in Kuwait time, not UTC. Every stored timestamp stays UTC, but
        // the human-readable half of a human-readable number must match the day the buyer
        // and the operator remember: 01:30 in Kuwait is still yesterday in UTC.
        var local = now.ToOffset(KuwaitOffset);

        Span<char> buffer = stackalloc char[16];
        "KW-".CopyTo(buffer);
        WriteTwoDigits(buffer[3..], local.Year % 100);
        WriteTwoDigits(buffer[5..], local.Month);
        WriteTwoDigits(buffer[7..], local.Day);
        buffer[9] = '-';

        Span<byte> entropy = stackalloc byte[RandomLength];
        random.Fill(entropy);

        // The low five bits of a uniform byte are themselves uniform over 0..31, because
        // 256 is an exact multiple of 32. Rejection sampling here would be dead code that
        // never rejects; the bias it guards against cannot arise with this alphabet size.
        for (var i = 0; i < RandomLength; i++)
        {
            buffer[10 + i] = Alphabet[entropy[i] & 0x1F];
        }

        return new string(buffer);
    }

    private static void WriteTwoDigits(Span<char> destination, int value)
    {
        destination[0] = (char)('0' + (value / 10));
        destination[1] = (char)('0' + (value % 10));
    }
}
