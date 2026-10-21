using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Accounts;
using Nexus.Infrastructure.Persistence;

namespace Nexus.IntegrationTests;

/// <summary>
/// AC-7: a guest's purchases join their account when the address is confirmed, and
/// <b>only</b> then.
/// <para>
/// The second test is the one that matters. If the merge fired at registration, typing a
/// stranger's address into the form would hand over their books, and the attacker would
/// not even have to receive the message.
/// </para>
/// </summary>
[Collection(NexusCollection.Name)]
public sealed class AccountsClaimTests(NexusApp app) : IDisposable
{
    private readonly AccountsHost _host = new(app);
    private const string Password = "correct-horse-battery";

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task An_unconfirmed_registration_claims_nothing()
    {
        const string email = "claim-victim@example.com";
        var order = await _host.PaidGuestOrderAsync(email, "Noura Al-Sabah", "VICTIM");

        var attacker = _host.Client();
        var registered = await attacker.PostAsJsonAsync("/api/account/register",
            new { name = "Not Noura", email, password = Password, language = "en" });
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);

        // Nothing was linked: the customer row that owns the order is still a guest's.
        using var scope = _host.Scope();
        var db = _host.Service<NexusDbContext>(scope);

        var customer = await db.Customers.AsNoTracking()
            .FirstAsync(c => c.NormalisedEmail == Customer.Normalise(email));
        Assert.Null(customer.IdentityUserId);

        var user = await _host.UserAsync(email);
        Assert.Null(user.CustomerId);
        Assert.False(user.EmailConfirmed);

        // And the order is still filed against the guest row, untouched.
        var stored = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
        Assert.Equal(customer.Id, stored.CustomerId);
    }

    [Fact]
    public async Task An_unconfirmed_registration_cannot_reach_the_library_at_all()
    {
        const string email = "claim-noreach@example.com";
        await _host.PaidGuestOrderAsync(email, "Salem Al-Harbi", "NOREACH", bookId: 2);

        var attacker = _host.Client();
        await attacker.PostAsJsonAsync("/api/account/register",
            new { name = "Not Salem", email, password = Password, language = "en" });

        // Sign-in is refused, so there is no session with which to ask for the library,
        // and the library endpoint refuses the anonymous attempt as well.
        var signIn = await attacker.PostAsJsonAsync("/api/account/signin", new { email, password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, signIn.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await attacker.GetAsync("/api/me/library")).StatusCode);
    }

    [Fact]
    public async Task A_confirmed_registration_claims_every_earlier_guest_order()
    {
        const string email = "claim-owner@example.com";
        var first = await _host.PaidGuestOrderAsync(email, "Jassim Al-Kandari", "OWNER1", bookId: 3);
        var second = await _host.PaidGuestOrderAsync(email, "Jassim Al-Kandari", "OWNER2", bookId: 4);

        var client = _host.Client();
        await client.PostAsJsonAsync("/api/account/register",
            new { name = "Jassim Al-Kandari", email, password = Password, language = "en" });

        var confirmed = await _host.ConfirmAsync(client, email);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        var claimed = JsonDocument.Parse(await confirmed.Content.ReadAsStringAsync())
            .RootElement.GetProperty("claimedOrders").GetInt32();
        Assert.Equal(2, claimed);

        var signedIn = await client.PostAsJsonAsync("/api/account/signin", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);

        var library = JsonDocument.Parse(await (await client.GetAsync("/api/me/library")).Content.ReadAsStringAsync())
            .RootElement;
        Assert.Equal(2, library.GetArrayLength());

        var orders = JsonDocument.Parse(await (await client.GetAsync("/api/me/orders")).Content.ReadAsStringAsync())
            .RootElement.EnumerateArray().Select(o => o.GetProperty("orderNumber").GetString()).ToList();

        Assert.Contains(first.OrderNumber, orders);
        Assert.Contains(second.OrderNumber, orders);
    }

    [Fact]
    public async Task Confirming_twice_claims_the_same_orders_once_and_does_not_fail()
    {
        const string email = "claim-twice@example.com";
        await _host.PaidGuestOrderAsync(email, "Reem Al-Duaij", "TWICE", bookId: 5);

        var client = _host.Client();
        await client.PostAsJsonAsync("/api/account/register",
            new { name = "Reem Al-Duaij", email, password = Password, language = "en" });

        var first = await _host.ConfirmAsync(client, email);
        var second = await _host.ConfirmAsync(client, email);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        Assert.Equal(1, Claimed(await first.Content.ReadAsStringAsync()));
        // Nothing is inherited a second time; the first confirmation already did it.
        Assert.Equal(0, Claimed(await second.Content.ReadAsStringAsync()));
    }

    /// <summary>
    /// The claim is refused outright when the address is not confirmed, even if the
    /// service is called directly. The endpoint's ordering is the first guard; this is
    /// the one that survives the next caller.
    /// </summary>
    [Fact]
    public async Task The_claim_service_refuses_an_unconfirmed_user()
    {
        const string email = "claim-direct@example.com";
        await _host.PaidGuestOrderAsync(email, "Bader Al-Mansour", "DIRECT", bookId: 6);

        var client = _host.Client();
        await client.PostAsJsonAsync("/api/account/register",
            new { name = "Bader Al-Mansour", email, password = Password, language = "en" });

        using var scope = _host.Scope();
        var users = _host.Service<UserManager<NexusUser>>(scope);
        var claim = _host.Service<GuestOrderClaim>(scope);

        var user = await users.FindByEmailAsync(email);
        var (result, problem) = await claim.ClaimAsync(user!);

        Assert.Null(result);
        Assert.Equal("not_confirmed", problem!.Code);
    }

    /// <summary>
    /// A buyer who checks out as a guest <i>after</i> confirming still lands in the right
    /// library: the order resolves to the customer row the login already owns.
    /// </summary>
    [Fact]
    public async Task An_order_placed_after_confirmation_is_in_the_library_too()
    {
        const string email = "claim-later@example.com";
        var (client, _) = await _host.SignedInCustomerAsync(email, "Shaikha Al-Roumi", Password);

        var before = JsonDocument.Parse(await (await client.GetAsync("/api/me/library")).Content.ReadAsStringAsync())
            .RootElement.GetArrayLength();
        Assert.Equal(0, before);

        await _host.PaidGuestOrderAsync(email, "Shaikha Al-Roumi", "LATER", bookId: 7);

        var after = JsonDocument.Parse(await (await client.GetAsync("/api/me/library")).Content.ReadAsStringAsync())
            .RootElement.GetArrayLength();
        Assert.Equal(1, after);
    }

    private static int Claimed(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("claimedOrders").GetInt32();
}
