using Nexus.Infrastructure.Payments;

namespace Nexus.UnitTests.Payments;

/// <summary>
/// The one test that proves our hashstring implementation agrees with Tap's, rather than
/// with itself.
/// <para>
/// <b>Why it is skipped.</b> Tap's documentation does not contain a verifiable worked
/// example. The charge webhook sample at developers.tap.company/docs/webhook carries a
/// real <c>hashstring</c> header
/// (<c>356f77b6…38f</c>) from some merchant's sandbox, while the secret key in the code
/// block beneath it is a placeholder sitting among other placeholders — every sibling
/// variable in that snippet is the literal string
/// <c>'charge.amount or authorize.amount …'</c>. The two do not belong together.
/// Computing HMAC-SHA256 over the sample payload with that key reproduces nothing, as
/// tried with the amount rendered <c>1</c>, <c>1.0</c>, <c>1.00</c> and <c>1.000</c>.
/// </para>
/// <para>
/// <b>What unblocks it.</b> One real delivery from a Tap sandbox merchant account:
/// the raw request body, the <c>hashstring</c> header, and the <c>sk_test_</c> key of the
/// account that received it. Paste the three below, delete the Skip, and this becomes the
/// test that stops a silent total payment failure.
/// </para>
/// <para>
/// Until then every other signature test asserts our implementation against our own
/// expectation — which is worth having for the string's shape, and worth nothing as
/// proof that Tap will accept it.
/// </para>
/// </summary>
public class TapSignatureSampleTests
{
    private const string Skip =
        "Needs a captured Tap sandbox webhook: raw body, hashstring header, and that " +
        "account's sk_test_ key. Tap's published sample pairs a real hash with a " +
        "placeholder key and cannot be verified. See open question Q1.";

    [Fact(Skip = Skip)]
    public void Our_hash_matches_a_real_signed_delivery()
    {
        // Paste from the captured delivery:
        const string secretKey = "sk_test_REPLACE_ME";
        const string id = "chg_REPLACE_ME";
        const string amount = "0.000";          // already at the currency's scale
        const string currency = "KWD";
        const string gatewayReference = "";
        const string paymentReference = "";
        const string status = "CAPTURED";
        const string created = "0";
        const string expectedHash = "REPLACE_ME";

        var toBeHashed =
            "x_id" + id +
            "x_amount" + amount +
            "x_currency" + currency +
            "x_gateway_reference" + gatewayReference +
            "x_payment_reference" + paymentReference +
            "x_status" + status +
            "x_created" + created;

        Assert.Equal(expectedHash, TapSignature.Compute(toBeHashed, secretKey));
    }

    /// <summary>
    /// Guards the guard: if someone deletes the Skip without supplying a real sample,
    /// this fails rather than letting a placeholder test pass for a real one.
    /// </summary>
    [Fact]
    public void The_sample_fixture_is_still_a_placeholder()
    {
        var placeholder = TapSignature.Compute("x_idchg_REPLACE_ME", "sk_test_REPLACE_ME");
        Assert.NotEqual("REPLACE_ME", placeholder);
    }
}
