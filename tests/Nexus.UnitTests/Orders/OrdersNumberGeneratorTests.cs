using System.Text.RegularExpressions;
using Nexus.Infrastructure.Orders;

namespace Nexus.UnitTests.Orders;

public sealed class OrdersNumberGeneratorTests
{
    private static readonly Regex Shape =
        new("^KW-[0-9]{6}-[0123456789ABCDEFGHJKMNPQRSTVWXYZ]{6}$", RegexOptions.Compiled);

    /// <summary>Deterministic bytes, so the format and the distribution can be asserted.</summary>
    private sealed class SeededBytes(int seed) : IRandomBytes
    {
        private readonly Random _random = new(seed);

        public void Fill(Span<byte> destination)
        {
            var buffer = new byte[destination.Length];
            _random.NextBytes(buffer);
            buffer.CopyTo(destination);
        }
    }

    /// <summary>Hands back the same byte for every position, to force a collision.</summary>
    private sealed class FixedBytes(byte value) : IRandomBytes
    {
        public void Fill(Span<byte> destination) => destination.Fill(value);
    }

    [Fact]
    public void The_number_is_sixteen_characters_in_the_agreed_shape()
    {
        var number = new OrderNumberGenerator(new SeededBytes(1)).Next(DateTimeOffset.UtcNow);

        Assert.Equal(16, number.Length);
        Assert.Matches(Shape, number);
    }

    // Tap's reference field is the ceiling the format was chosen against.
    [Fact]
    public void The_number_fits_inside_twenty_characters()
    {
        var generator = new OrderNumberGenerator(new SeededBytes(7));

        for (var i = 0; i < 500; i++)
        {
            Assert.True(generator.Next(DateTimeOffset.UtcNow).Length <= 20);
        }
    }

    // The date half of a human-readable number must match the day the buyer remembers.
    // 22:30 UTC on the 20th is 01:30 on the 21st in Kuwait, which has no DST.
    [Theory]
    [InlineData("2026-10-20T22:30:00+00:00", "KW-261021-")]
    [InlineData("2026-10-21T20:59:59+00:00", "KW-261021-")]
    [InlineData("2026-10-21T21:00:00+00:00", "KW-261022-")]
    [InlineData("2026-01-02T03:00:00+00:00", "KW-260102-")]
    public void The_date_is_in_Kuwait_time(string instant, string expectedPrefix)
    {
        var number = new OrderNumberGenerator(new SeededBytes(3))
            .Next(DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture));

        Assert.StartsWith(expectedPrefix, number, StringComparison.Ordinal);
    }

    // An input already expressed in a different offset must not shift the date.
    [Fact]
    public void An_offset_input_is_converted_not_reinterpreted()
    {
        var kuwait = new DateTimeOffset(2026, 10, 21, 1, 30, 0, TimeSpan.FromHours(3));

        Assert.StartsWith("KW-261021-",
            new OrderNumberGenerator(new SeededBytes(3)).Next(kuwait), StringComparison.Ordinal);
    }

    // I, L, O and U are the characters this number exists to avoid: they are misread
    // over the phone, which is the number's main job.
    [Fact]
    public void The_alphabet_excludes_the_ambiguous_letters()
    {
        foreach (var excluded in "ILOU")
        {
            Assert.DoesNotContain(excluded, OrderNumberGenerator.Alphabet);
        }

        Assert.Equal(32, OrderNumberGenerator.Alphabet.Length);
        Assert.Equal(OrderNumberGenerator.Alphabet.Length,
            OrderNumberGenerator.Alphabet.Distinct().Count());
    }

    // C-ordnum-test: 2,000 distinct draws from a seeded RNG. "100,000 generations
    // produce no duplicate" fails 99 runs in 100 by the format's own arithmetic, and
    // the fix a developer reaches for is deleting the assertion.
    [Fact]
    public void Two_thousand_draws_in_one_day_are_distinct()
    {
        var generator = new OrderNumberGenerator(new SeededBytes(2026));
        var at = new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

        var drawn = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 2_000; i++)
        {
            Assert.True(drawn.Add(generator.Next(at)), "A draw repeated inside one day-prefix.");
        }
    }

    // The other half of C-ordnum-test: uniqueness is cheap to fake with a biased
    // generator, so assert the distribution too. 200,000 draws put ~6,250 of each
    // character in each position; six sigma is about 467.
    [Fact]
    public void Every_character_appears_evenly_in_every_position()
    {
        const int draws = 200_000;
        const int positions = OrderNumberGenerator.RandomLength;

        var generator = new OrderNumberGenerator(new SeededBytes(99));
        var at = new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
        var counts = new int[positions, 32];

        for (var i = 0; i < draws; i++)
        {
            var number = generator.Next(at);
            for (var p = 0; p < positions; p++)
            {
                counts[p, OrderNumberGenerator.Alphabet.IndexOf(number[10 + p])]++;
            }
        }

        const int expected = draws / 32;
        const int tolerance = 500;

        for (var p = 0; p < positions; p++)
        {
            for (var c = 0; c < 32; c++)
            {
                Assert.InRange(counts[p, c], expected - tolerance, expected + tolerance);
            }
        }
    }

    [Fact]
    public void A_degenerate_random_source_collides_so_the_unique_index_is_the_authority()
    {
        var generator = new OrderNumberGenerator(new FixedBytes(0x00));
        var at = new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal("KW-261021-000000", generator.Next(at));
        Assert.Equal(generator.Next(at), generator.Next(at));
    }
}
