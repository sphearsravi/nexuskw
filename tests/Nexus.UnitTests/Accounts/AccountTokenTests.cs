using Nexus.Infrastructure.Accounts;

namespace Nexus.UnitTests.Accounts;

public sealed class AccountTokenTests
{
    [Theory]
    [InlineData("CfDJ8+simple/token==")]
    [InlineData("short")]
    [InlineData("مرحبا")]
    public void A_token_survives_the_round_trip(string token) =>
        Assert.Equal(token, AccountTokens.Decode(AccountTokens.Encode(token)));

    [Fact]
    public void The_encoded_form_is_url_safe()
    {
        // The whole reason base64url is used: +, / and = are what a mail client's link
        // rewriter and the +-means-space rule between them corrupt.
        var encoded = AccountTokens.Encode("CfDJ8+a/b+c/d==");

        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!not base64 at all!!!")]
    public void Rubbish_decodes_to_null_rather_than_throwing(string? value) =>
        Assert.Null(AccountTokens.Decode(value));
}
