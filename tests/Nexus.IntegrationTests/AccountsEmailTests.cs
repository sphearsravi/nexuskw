using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Nexus.Application.Email;
using Nexus.Domain;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Persistence;

namespace Nexus.IntegrationTests;

/// <summary>
/// The account emails: that they are queued at all, that the default transport suppresses
/// them visibly rather than silently, and that the body is built at dispatch with a live
/// token in it.
/// </summary>
[Collection(NexusCollection.Name)]
public sealed class AccountsEmailTests(NexusApp app) : IDisposable
{
    private readonly AccountsHost _host = new(app);
    private const string Password = "correct-horse-battery";

    public void Dispose() => _host.Dispose();

    /// <summary>
    /// Transport is None by default, which is correct until a provider is chosen. The
    /// row is still written and marked Suppressed: an operator must be able to see that
    /// this customer was not emailed, and a missing row is indistinguishable from a bug.
    /// </summary>
    [Fact]
    public async Task Registering_queues_a_confirmation_that_is_visibly_suppressed()
    {
        const string email = "mail-register@example.com";
        var client = _host.Client();

        var response = await client.PostAsJsonAsync("/api/account/register",
            new { name = "Maryam Al-Qallaf", email, password = Password, language = "ar" });
        response.EnsureSuccessStatusCode();

        Assert.Contains("\"delivery\":\"suppressed\"", await response.Content.ReadAsStringAsync());

        using var scope = _host.Scope();
        var db = _host.Service<NexusDbContext>(scope);

        var row = await db.OutboxEmails.AsNoTracking()
            .FirstAsync(e => e.ToEmail == email && e.Kind == EmailKind.AccountConfirm);

        Assert.Equal(EmailStatus.Suppressed, row.Status);
        Assert.Equal("ar", row.Language);
        Assert.Null(row.OrderId);
    }

    [Fact]
    public async Task A_reset_request_for_a_confirmed_account_queues_a_reset_and_nothing_for_an_unknown_one()
    {
        const string known = "mail-reset@example.com";
        await _host.SignedInCustomerAsync(known, "Talal Al-Enezi", Password);

        var client = _host.Client();
        await client.PostAsJsonAsync("/api/account/forgot", new { email = known, language = "en" });
        await client.PostAsJsonAsync("/api/account/forgot",
            new { email = "mail-nobody@example.com", language = "en" });

        using var scope = _host.Scope();
        var db = _host.Service<NexusDbContext>(scope);

        Assert.True(await db.OutboxEmails.AnyAsync(e => e.ToEmail == known && e.Kind == EmailKind.PasswordReset));
        Assert.False(await db.OutboxEmails.AnyAsync(e => e.ToEmail == "mail-nobody@example.com"));
    }

    /// <summary>
    /// The body is produced at dispatch, so the token in it is minted then and not at
    /// enqueue. Nothing credential-bearing is stored, and a message retried after a long
    /// queue delay carries a link that is live rather than one that expired in the queue.
    /// </summary>
    [Fact]
    public async Task The_confirmation_body_is_rendered_at_dispatch_with_a_working_link()
    {
        const string email = "mail-render@example.com";
        var client = _host.Client();

        await client.PostAsJsonAsync("/api/account/register",
            new { name = "Fatima Al-Saleh", email, password = Password, language = "en" });

        using var scope = _host.Scope();
        var db = _host.Service<NexusDbContext>(scope);
        var renderer = _host.Service<IEmailTemplateRenderer>(scope);

        var row = await db.OutboxEmails.AsNoTracking()
            .FirstAsync(e => e.ToEmail == email && e.Kind == EmailKind.AccountConfirm);

        // The stored row carries no body and no token; that is what makes a backup of
        // this table harmless.
        Assert.DoesNotContain("confirm.html", System.Text.Json.JsonSerializer.Serialize(row));

        var (rendered, problem) = await renderer.RenderAsync(row);

        Assert.Null(problem);
        Assert.NotNull(rendered);
        Assert.Contains("/confirm.html#u=", rendered!.Html, StringComparison.Ordinal);
        Assert.Contains("/confirm.html#u=", rendered.Text, StringComparison.Ordinal);

        // The plain-text alternative is never optional: some Gulf corporate gateways
        // strip HTML, and a link only in the HTML part is a link nobody can follow.
        Assert.False(string.IsNullOrWhiteSpace(rendered.Text));
    }

    [Fact]
    public async Task An_arabic_registration_renders_an_arabic_confirmation()
    {
        const string email = "mail-arabic@example.com";
        var client = _host.Client();

        await client.PostAsJsonAsync("/api/account/register",
            new { name = "سارة المطيري", email, password = Password, language = "ar" });

        using var scope = _host.Scope();
        var db = _host.Service<NexusDbContext>(scope);
        var renderer = _host.Service<IEmailTemplateRenderer>(scope);

        var row = await db.OutboxEmails.AsNoTracking()
            .FirstAsync(e => e.ToEmail == email && e.Kind == EmailKind.AccountConfirm);

        var (rendered, _) = await renderer.RenderAsync(row);

        Assert.NotNull(rendered);
        Assert.Contains("dir=\"rtl\"", rendered!.Html, StringComparison.Ordinal);
        Assert.Equal(
            Nexus.Infrastructure.Accounts.AccountEmailText
                .For(EmailKind.AccountConfirm, "ar", "Nexus").Subject,
            rendered.Subject);
    }

    /// <summary>
    /// Confirming before the message goes out makes it pointless, and sending it anyway
    /// would land after the customer had already finished.
    /// </summary>
    [Fact]
    public async Task A_confirmation_for_an_already_confirmed_address_refuses_to_render()
    {
        const string email = "mail-already@example.com";
        await _host.SignedInCustomerAsync(email, "Abdullah Al-Khaled", Password);

        using var scope = _host.Scope();
        var db = _host.Service<NexusDbContext>(scope);
        var renderer = _host.Service<IEmailTemplateRenderer>(scope);

        var row = await db.OutboxEmails.AsNoTracking()
            .FirstAsync(e => e.ToEmail == email && e.Kind == EmailKind.AccountConfirm);

        var (rendered, problem) = await renderer.RenderAsync(row);

        Assert.Null(rendered);
        Assert.Equal("already_confirmed", problem);
    }

    /// <summary>The decorator must still answer for everything it does not own.</summary>
    [Fact]
    public async Task The_order_emails_still_render_through_the_same_renderer()
    {
        const string email = "mail-order@example.com";
        var order = await _host.PaidGuestOrderAsync(email, "Hussain Al-Baghli", "MAILORD", bookId: 18);

        using var scope = _host.Scope();
        var db = _host.Service<NexusDbContext>(scope);
        var renderer = _host.Service<IEmailTemplateRenderer>(scope);

        var customer = await db.Customers.AsNoTracking()
            .FirstAsync(c => c.NormalisedEmail == Customer.Normalise(email));

        var row = new OutboxEmail
        {
            Kind = EmailKind.OrderConfirmation,
            OrderId = order.Id,
            CustomerId = customer.Id,
            ToEmail = email,
            Language = "en",
            IdempotencyKey = "test-order-render",
            PublicRef = "test-order-render",
            CreatedAt = DateTimeOffset.UtcNow
        };

        var (rendered, problem) = await renderer.RenderAsync(row);

        Assert.Null(problem);
        Assert.NotNull(rendered);
        Assert.Contains(order.OrderNumber, rendered!.Html, StringComparison.Ordinal);
    }
}
