using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Application.Email;
using Nexus.Domain;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Email;
using Nexus.Infrastructure.Persistence;
using Nexus.Web.Controllers.Admin;

namespace Nexus.IntegrationTests;

/// <summary>
/// The renderer against real order rows and real grants.
/// <para>
/// Stage C's <c>DeliveryService</c> is being built in a checkout this stage cannot see, so
/// the plaintext token comes from a fake standing in for
/// <c>IDeliveryService.UnprotectToken</c>. Everything on this side of that call — the
/// grant query, the per-book newest-sequence rule, the snapshots and the refusal — is the
/// real thing against SQL Server.
/// </para>
/// </summary>
[Collection(NexusCollection.Name)]
public sealed class EmailRendererStageDTests(NexusApp app)
{
    private readonly NexusApp _app = app;

    private static Task<EmailStageDHost> HostAsync() => new EmailStageDHost().ResetAsync();

    [Fact]
    public async Task A_paid_order_renders_a_landing_page_link_per_book()
    {
        using var host = await HostAsync();
        var (order, _, token) = await EmailStageDData.PaidOrderAsync(host);

        var (email, problem) = await RenderAsync(host, order.Id, EmailKind.OrderConfirmation, "en");

        Assert.Null(problem);
        Assert.NotNull(email);
        Assert.Contains($"https://nexus.example/download.html?t={token}", email!.Text, StringComparison.Ordinal);
        Assert.DoesNotContain($"/download/{token}", email.Text, StringComparison.Ordinal);
        Assert.Contains(order.OrderNumber, email.Subject, StringComparison.Ordinal);
    }

    /// <summary>
    /// R4's whole point. The outbox holds no body, so a retry days later re-reads the
    /// grants — and an expired one is refused rather than mailed as a dead link.
    /// </summary>
    [Fact]
    public async Task A_retry_after_the_grant_expires_is_refused_rather_than_sending_a_dead_link()
    {
        using var host = await HostAsync();
        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host);

        var (before, _) = await RenderAsync(host, order.Id, EmailKind.OrderConfirmation, "en");
        Assert.NotNull(before);

        host.Clock.Advance(TimeSpan.FromDays(8));

        var (after, problem) = await RenderAsync(host, order.Id, EmailKind.OrderConfirmation, "en");
        Assert.Null(after);
        Assert.Equal("no_live_grants", problem);
    }

    [Fact]
    public async Task A_grant_whose_token_cannot_be_recovered_is_omitted_rather_than_linked_empty()
    {
        using var host = await HostAsync();
        var (order, grant, _) = await EmailStageDData.PaidOrderAsync(host);
        host.Delivery.Tokens.Remove(grant.Id);

        var (email, problem) = await RenderAsync(host, order.Id, EmailKind.OrderConfirmation, "en");

        Assert.Null(email);
        Assert.Equal("no_live_grants", problem);
    }

    [Fact]
    public async Task An_unpaid_order_is_refused()
    {
        using var host = await HostAsync();
        var (order, _, _) = await EmailStageDData.PaidOrderAsync(
            host, status: OrderStatus.Pending, withGrant: false);

        var (email, problem) = await RenderAsync(host, order.Id, EmailKind.OrderConfirmation, "en");

        Assert.Null(email);
        Assert.Equal("order_not_paid", problem);
    }

    /// <summary>The Arabic edition reads the Arabic snapshot, not the English one.</summary>
    [Fact]
    public async Task The_Arabic_edition_uses_the_Arabic_title_snapshot()
    {
        using var host = await HostAsync();
        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host, language: "ar", bookId: 1);

        var arabicTitle = await host.InScopeAsync(sp => sp.GetRequiredService<NexusDbContext>()
            .OrderItems.AsNoTracking().Where(i => i.OrderId == order.Id)
            .Select(i => i.TitleArSnapshot).FirstAsync());

        var (email, _) = await RenderAsync(host, order.Id, EmailKind.OrderConfirmation, "ar");

        Assert.NotNull(email);
        Assert.Contains(arabicTitle, email!.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The prose describes the grant the reader holds. An operator shortening the window
    /// after a sale must not make last week's email retroactively wrong.
    /// </summary>
    [Fact]
    public async Task The_expiry_prose_follows_the_grant_not_the_current_setting()
    {
        using var host = await HostAsync();
        host.Settings.Set("delivery.linkExpiryDays", "365");

        var (order, _, _) = await EmailStageDData.PaidOrderAsync(
            host, expiresAt: host.Clock.GetUtcNow().AddDays(7));

        var (email, _) = await RenderAsync(host, order.Id, EmailKind.OrderConfirmation, "en");

        Assert.NotNull(email);
        Assert.Contains("7 days", email!.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("365 days", email.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_operators_edited_template_is_used()
    {
        using var host = await HostAsync();
        host.Settings.Set("email.confirmation.subject.en", "Thanks! {{order_number}} is ready");

        var (order, _, _) = await EmailStageDData.PaidOrderAsync(host);
        var (email, _) = await RenderAsync(host, order.Id, EmailKind.OrderConfirmation, "en");

        Assert.Equal($"Thanks! {order.OrderNumber} is ready", email!.Subject);
    }

    private static Task<(RenderedEmail? Email, string? Problem)> RenderAsync(
        EmailStageDHost host, int orderId, EmailKind kind, string language) =>
        host.InScopeAsync(sp => sp.GetRequiredService<IEmailTemplateRenderer>().RenderAsync(new OutboxEmail
        {
            OrderId = orderId,
            Kind = kind,
            Language = language,
            ToEmail = "reader@example.com",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            PublicRef = Guid.NewGuid().ToString("N")[..22]
        }));
}

/// <summary>
/// The file drop writes a real RFC 5322 message, because an <c>.eml</c> opens in Outlook
/// and Apple Mail and that double-click is the only way anyone actually checks the Arabic.
/// </summary>
public sealed class FileDropSenderStageDTests
{
    [Fact]
    public async Task An_Arabic_message_round_trips_as_UTF8_with_both_parts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nexus-stage-d-drop", Guid.NewGuid().ToString("N"));

        var sender = new FileDropEmailSender(
            Microsoft.Extensions.Options.Options.Create(new EmailOptions
            {
                Transport = EmailTransport.FileDrop,
                FileDrop = new FileDropOptions { Path = directory },
                BounceDomain = "nexus.example"
            }),
            new EmailIdentity(new StageDSettings()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FileDropEmailSender>.Instance,
            TimeProvider.System);

        var result = await sender.SendAsync(new OutgoingEmail(
            "noura@example.com", "نورة العلي",
            "طلبك ⁨KW-261021-7F3K2M⁩ — رابط التحميل",
            "<html dir=\"rtl\"><body><p>الإجمالي 6.500 د.ك</p></body></html>",
            "الإجمالي 6.500 د.ك",
            "abc123@nexus.example",
            "bounces+abc123@nexus.example"));

        Assert.Equal(EmailSendOutcome.Sent, result.Outcome);

        var file = Assert.Single(Directory.GetFiles(directory, "*.eml"));
        var message = await MimeKit.MimeMessage.LoadAsync(file);

        // The failure this guards against is a fallback to ISO-8859-1, which is how
        // Arabic becomes "????" without anything else looking wrong.
        Assert.Equal("طلبك ⁨KW-261021-7F3K2M⁩ — رابط التحميل", message.Subject);
        Assert.Contains("6.500 د.ك", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("6.500 د.ك", message.HtmlBody, StringComparison.Ordinal);
        Assert.Equal("نورة العلي", message.To.Mailboxes.Single().Name);

        Directory.Delete(directory, recursive: true);
    }
}

/// <summary>
/// The admin endpoints are built and tested but not yet reachable: Stage 0's
/// <c>Program.cs</c> registers the policies and the authentication scheme, and this stage
/// does not edit that file. What can be asserted now is that the attributes say what the
/// contract says they say, so the two sides cannot drift apart silently.
/// </summary>
public sealed class AdminEmailAuthorisationTests
{
    [Fact]
    public void The_resend_endpoint_is_admin_or_staff() =>
        Assert.Equal(AdminPolicies.AdminOrStaff, PolicyOn(typeof(AdminEmailController)));

    [Fact]
    public void The_suppression_list_is_admin_only() =>
        Assert.Equal(AdminPolicies.AdminOnly, PolicyOn(typeof(AdminSuppressionController)));

    // The operations view and the retry are a wider blast radius than a resend, and the
    // sending domain's reputation is the asset being protected.
    [Theory]
    [InlineData(nameof(AdminEmailController.Outbox))]
    [InlineData(nameof(AdminEmailController.Retry))]
    public void The_operations_endpoints_are_admin_only(string method)
    {
        var attribute = typeof(AdminEmailController).GetMethod(method)!
            .GetCustomAttribute<AuthorizeAttribute>();

        Assert.Equal(AdminPolicies.AdminOnly, attribute?.Policy);
    }

    // Cookie authentication plus a mutating verb is a CSRF target by construction.
    [Theory]
    [InlineData(typeof(AdminEmailController))]
    [InlineData(typeof(AdminSuppressionController))]
    public void Every_admin_controller_validates_antiforgery(Type controller) =>
        Assert.NotNull(controller.GetCustomAttribute<AutoValidateAntiforgeryTokenAttribute>());

    private static string? PolicyOn(Type controller) =>
        controller.GetCustomAttribute<AuthorizeAttribute>()?.Policy;
}
