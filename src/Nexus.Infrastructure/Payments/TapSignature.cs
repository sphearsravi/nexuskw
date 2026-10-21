using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Nexus.Application.Payments;
using Nexus.Domain;

namespace Nexus.Infrastructure.Payments;

public sealed class TapOptions
{
    public string BaseUrl { get; set; } = "https://api.tap.company/v2";
    public string SecretKey { get; set; } = "";
    public string RedirectUrl { get; set; } = "";
    public string WebhookUrl { get; set; } = "";
    public int AbandonAfterHours { get; set; } = 24;
}

/// <summary>
/// Verifies the <c>hashstring</c> header on a Tap notification.
/// <para>
/// The concatenation below is copied from Tap's own documentation
/// (developers.tap.company/docs/webhook, "Validate the webhook (hashstring)"), where it
/// reads:
/// </para>
/// <code>
/// $toBeHashedString = 'x_id' . $id . 'x_amount' . $amount . 'x_currency' . $currency
///   . 'x_gateway_reference' . $gateway_reference . 'x_payment_reference' . $payment_reference
///   . 'x_status' . $status . 'x_created' . $created . '';
/// </code>
/// <para>
/// <b>The amount is formatted at the currency's own scale.</b> KWD has three decimals.
/// Hash <c>6.5</c> where Tap hashed <c>6.500</c> and every signature fails — silently,
/// with nothing wrong on the storefront and no order ever completing. That is why the
/// amount arrives here as a <see cref="decimal"/> plus a <see cref="Currency"/> rather
/// than as a pre-rendered string: the caller cannot get the scale wrong by accident.
/// </para>
/// </summary>
public sealed class TapSignature(IOptions<TapOptions> options) : ITapSignature
{
    private readonly string _secretKey = options.Value.SecretKey;

    public string BuildToBeHashed(
        string id, decimal amount, Currency currency,
        string gatewayReference, string paymentReference, string status, string created) =>
        "x_id" + id +
        "x_amount" + currency.Format(amount) +
        // Tap's sample passes an empty string when the gateway reference is absent —
        // not null, and not an omitted segment.
        "x_currency" + currency.Code +
        "x_gateway_reference" + (gatewayReference ?? "") +
        "x_payment_reference" + (paymentReference ?? "") +
        "x_status" + status +
        "x_created" + created;

    public bool Verify(string toBeHashed, string providedHash)
    {
        if (string.IsNullOrEmpty(providedHash)) return false;
        if (string.IsNullOrEmpty(_secretKey))
            throw new InvalidOperationException(
                "Tap secret key is not configured. Webhooks cannot be verified, so none may be trusted.");

        var expected = Compute(toBeHashed, _secretKey);

        // Fixed-time: a timing oracle on a signature check is a real attack, and the
        // comparison costs nothing either way.
        var a = Encoding.ASCII.GetBytes(expected);
        var b = Encoding.ASCII.GetBytes(providedHash.Trim().ToLowerInvariant());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>Lowercase hex HMAC-SHA256, matching PHP's <c>hash_hmac('sha256', …)</c>.</summary>
    public static string Compute(string toBeHashed, string secretKey)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(toBeHashed))).ToLowerInvariant();
    }
}
