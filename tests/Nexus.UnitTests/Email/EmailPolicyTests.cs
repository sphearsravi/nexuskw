using Nexus.Application.Email;
using Nexus.Infrastructure.Email;

namespace Nexus.UnitTests.Email;

public class SmtpFailureClassifierTests
{
    // 5.7.1, 5.7.23 and 5.7.26 are returned at RCPT TO exactly as 5.1.1 is. Classifying on
    // the command rather than the enhanced code would suppress every customer who bought
    // on the first day of real sending, while SPF was still propagating, with /health green.
    [Theory]
    [InlineData("550 5.7.1 Unauthenticated email is not accepted")]
    [InlineData("550 5.7.23 SPF validation failed")]
    [InlineData("550 5.7.26 Unauthenticated email from example.com is not accepted")]
    public void A_sender_policy_rejection_is_permanent_but_never_suppresses(string diagnostic)
    {
        var result = SmtpFailureClassifier.Classify(550, diagnostic);

        Assert.Equal(EmailSendOutcome.PermanentFailure, result.Outcome);
        Assert.Equal(FailureKind.SenderPolicy, result.Kind);
        Assert.False(result.IsSuppressionCandidate);
    }

    [Theory]
    [InlineData("550 5.1.1 The email account that you tried to reach does not exist")]
    [InlineData("550 5.1.2 Bad destination system address")]
    [InlineData("550 5.1.10 Recipient address lookup failed")]
    public void A_recipient_hard_bounce_is_a_suppression_candidate(string diagnostic)
    {
        var result = SmtpFailureClassifier.Classify(550, diagnostic);

        Assert.Equal(EmailSendOutcome.PermanentFailure, result.Outcome);
        Assert.Equal(FailureKind.RecipientHardBounce, result.Kind);
        Assert.True(result.IsSuppressionCandidate);
    }

    [Fact]
    public void Another_permanent_rejection_blames_our_message_not_their_mailbox()
    {
        var result = SmtpFailureClassifier.Classify(554, "554 5.6.0 Message content rejected");

        Assert.Equal(EmailSendOutcome.PermanentFailure, result.Outcome);
        Assert.Equal(FailureKind.MessageRejected, result.Kind);
        Assert.False(result.IsSuppressionCandidate);
    }

    [Theory]
    [InlineData(421, "421 4.7.0 Try again later")]
    [InlineData(450, "450 4.2.1 The user you are trying to contact is receiving mail too quickly")]
    [InlineData(452, "452 4.2.2 The email account that you tried to reach is over quota")]
    public void A_four_hundred_is_transient(int status, string diagnostic)
    {
        var result = SmtpFailureClassifier.Classify(status, diagnostic);

        Assert.Equal(EmailSendOutcome.TransientFailure, result.Outcome);
        Assert.False(result.IsSuppressionCandidate);
    }

    // Loud, because every email in the store is about to fail the same way — but still a
    // retry, because the fix is a configuration change and the queue must survive it.
    [Fact]
    public void An_authentication_failure_is_transient()
    {
        var result = SmtpFailureClassifier.Classify(535, "535 5.7.8 Authentication credentials invalid");

        Assert.Equal(EmailSendOutcome.TransientFailure, result.Outcome);
        Assert.Equal(FailureKind.Transient, result.Kind);
    }

    // Without a code we cannot tell "no such user" from "your SPF is wrong", and only one
    // of those is the customer's fault.
    [Fact]
    public void A_bare_five_hundred_is_permanent_but_never_suppresses()
    {
        var result = SmtpFailureClassifier.Classify(550, "550 relaying denied");

        Assert.Equal(EmailSendOutcome.PermanentFailure, result.Outcome);
        Assert.False(result.IsSuppressionCandidate);
    }

    [Fact]
    public void A_network_failure_is_transient() =>
        Assert.Equal(EmailSendOutcome.TransientFailure, SmtpFailureClassifier.Network("dns").Outcome);
}

public class EmailRetryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static EmailRetryPolicy Policy() =>
        new(new FakeClock(Now));

    [Fact]
    public void The_schedule_is_one_five_fifteen_minutes_then_one_four_and_twelve_hours()
    {
        Assert.Equal(
            [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15),
             TimeSpan.FromHours(1), TimeSpan.FromHours(4), TimeSpan.FromHours(12)],
            EmailRetryPolicy.Schedule);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Every_retry_lands_inside_its_jittered_window(int attempt)
    {
        var policy = Policy();
        var (min, max) = EmailRetryPolicy.Window(attempt);

        for (var i = 0; i < 200; i++)
        {
            var next = policy.NextAttemptAt(attempt, new Random(i));
            Assert.NotNull(next);

            var delay = next.Value - Now;
            Assert.InRange(delay, min, max);
        }
    }

    // Without the spread, a provider coming back from an outage receives the whole backlog
    // in one burst and throttles us straight back into the same state.
    [Fact]
    public void The_jitter_actually_varies()
    {
        var policy = Policy();
        var draws = Enumerable.Range(0, 50).Select(i => policy.NextAttemptAt(1, new Random(i))).Distinct();

        Assert.True(draws.Count() > 10);
    }

    [Fact]
    public void The_schedule_terminates_rather_than_retrying_forever()
    {
        var policy = Policy();

        Assert.NotNull(policy.NextAttemptAt(EmailRetryPolicy.Schedule.Length, new Random(1)));
        Assert.Null(policy.NextAttemptAt(EmailRetryPolicy.Schedule.Length + 1, new Random(1)));
    }
}

public class VerpAddressTests
{
    [Fact]
    public void The_envelope_sender_names_the_message_that_caused_the_bounce()
    {
        var (returnPath, problem) = VerpAddress.ReturnPath("orders@nexus.example", "nexus.example", "abc123");

        Assert.Null(problem);
        Assert.Equal("bounces+abc123@nexus.example", returnPath);
    }

    [Fact]
    public void A_subdomain_bounce_host_aligns()
    {
        var (returnPath, problem) = VerpAddress.ReturnPath("orders@nexus.example", "bounce.nexus.example", "abc123");

        Assert.Null(problem);
        Assert.Equal("bounces+abc123@bounce.nexus.example", returnPath);
    }

    // DMARC aligns SPF on MAIL FROM, so an unaligned bounce domain quarantines every
    // message the store sends. Degrading to the From: address is strictly better than
    // being filed as spam by everyone.
    [Fact]
    public void An_unaligned_bounce_domain_is_refused_rather_than_used()
    {
        var (returnPath, problem) = VerpAddress.ReturnPath("orders@nexus.example", "bounces.other.test", "abc123");

        Assert.Null(returnPath);
        Assert.NotNull(problem);
        Assert.Contains("does not align", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_bounce_domain_degrades_quietly_to_the_from_address()
    {
        var (returnPath, problem) = VerpAddress.ReturnPath("orders@nexus.example", "", "abc123");

        Assert.Null(returnPath);
        Assert.NotNull(problem);
    }

    [Fact]
    public void The_message_id_is_stable_across_attempts() =>
        Assert.Equal("abc123@nexus.example", VerpAddress.MessageId("abc123", "nexus.example"));
}

public class EmailOptionsTests
{
    // The store must keep selling. Refusing to start over email is a worse failure than
    // not sending it, and Suppressed rows plus a red health check are not silence.
    [Fact]
    public void Smtp_without_a_host_degrades_to_None_rather_than_crashing()
    {
        var options = new EmailOptions { Transport = EmailTransport.Smtp };

        Assert.NotEmpty(options.Problems());
        Assert.Equal(EmailTransport.None, options.EffectiveTransport());
    }

    [Fact]
    public void The_default_transport_is_None() =>
        Assert.Equal(EmailTransport.None, new EmailOptions().EffectiveTransport());

    [Fact]
    public void A_configured_smtp_host_is_used()
    {
        var options = new EmailOptions
        {
            Transport = EmailTransport.Smtp,
            Smtp = new SmtpOptions { Host = "smtp.example", Port = 587 }
        };

        Assert.Empty(options.Problems());
        Assert.Equal(EmailTransport.Smtp, options.EffectiveTransport());
    }

    // A relative base URL ships "/download.html?t=…" into a mail client, which is a dead
    // link that looks perfectly fine in any test that renders the body to a string.
    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("nexus.example")]
    public void A_base_url_that_is_not_absolute_is_a_reported_problem(string value) =>
        Assert.NotEmpty(new SiteOptions { PublicBaseUrl = value }.Problems());

    [Fact]
    public void A_trailing_slash_cannot_double_up()
    {
        var site = new SiteOptions { PublicBaseUrl = "https://nexus.example/" };

        Assert.Empty(site.Problems());
        Assert.Equal("https://nexus.example", site.BaseOrFallback());
    }
}

public class SuppressionCircuitBreakerTests
{
    // Ten consecutive permanent failures across distinct recipients means the common
    // factor is us, not them. Suppression stops entirely rather than eating the customer
    // base over our own misconfiguration.
    [Fact]
    public void Ten_distinct_recipients_open_the_breaker()
    {
        var breaker = new SuppressionCircuitBreaker();

        for (var i = 0; i < 9; i++) breaker.RecordPermanentFailure($"user{i}@example.com");
        Assert.False(breaker.IsOpen);

        breaker.RecordPermanentFailure("user9@example.com");
        Assert.True(breaker.IsOpen);
    }

    [Fact]
    public void The_same_recipient_failing_repeatedly_does_not_open_it()
    {
        var breaker = new SuppressionCircuitBreaker();

        for (var i = 0; i < 50; i++) breaker.RecordPermanentFailure("one@example.com");

        Assert.False(breaker.IsOpen);
    }

    [Fact]
    public void One_success_closes_it()
    {
        var breaker = new SuppressionCircuitBreaker();
        for (var i = 0; i < 12; i++) breaker.RecordPermanentFailure($"user{i}@example.com");

        breaker.RecordSuccess();

        Assert.False(breaker.IsOpen);
        Assert.Equal(0, breaker.ConsecutivePermanentFailures);
    }
}

public class RecipientValidationTests
{
    [Theory]
    [InlineData("buyer@example.com")]
    [InlineData("noura.al-ali@sub.example.co")]
    public void A_plausible_address_is_accepted(string value) =>
        Assert.True(OrderEmailService.IsPlausibleAddress(value));

    // The header-injection cases matter more than the typo cases: a comma or a newline in
    // the recipient is how one resend becomes mail to an address nobody authorised.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nobody")]
    [InlineData("@example.com")]
    [InlineData("two@@example.com")]
    [InlineData("buyer@example")]
    [InlineData("buyer@example.com, attacker@evil.test")]
    [InlineData("buyer@example.com\r\nBcc: attacker@evil.test")]
    [InlineData("<buyer@example.com>")]
    public void An_implausible_or_injected_address_is_refused(string? value) =>
        Assert.False(OrderEmailService.IsPlausibleAddress(value));
}

internal sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
