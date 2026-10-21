using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Accounts;

namespace Nexus.IntegrationTests;

/// <summary>
/// Registration, sign-in, reset — and the refusals, which are the half worth testing.
/// </summary>
[Collection(NexusCollection.Name)]
public sealed class AccountsAuthTests(NexusApp app) : IDisposable
{
    private readonly AccountsHost _host = new(app);
    private const string Password = "correct-horse-battery";

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Registering_acknowledges_and_creates_an_unconfirmed_account()
    {
        var client = _host.Client();

        var response = await client.PostAsJsonAsync("/api/account/register", new
        {
            name = "Yousef Al-Rashid",
            email = "auth-new@example.com",
            password = Password,
            language = "ar"
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var user = await _host.UserAsync("auth-new@example.com");
        Assert.False(user.EmailConfirmed);
        Assert.Equal("ar", user.PreferredLanguage);
    }

    /// <summary>
    /// The enumeration test. A taken address and a free one must be indistinguishable:
    /// same status, same body, no error anywhere. Anything else is a membership oracle
    /// for every address an attacker cares to try.
    /// </summary>
    [Fact]
    public async Task Registering_an_existing_address_is_indistinguishable_from_a_new_one()
    {
        var first = _host.Client();
        var taken = "auth-taken@example.com";

        var fresh = await first.PostAsJsonAsync("/api/account/register",
            new { name = "Dana Al-Otaibi", email = taken, password = Password, language = "en" });

        var second = _host.Client();
        var duplicate = await second.PostAsJsonAsync("/api/account/register",
            new { name = "Somebody Else", email = taken, password = Password, language = "en" });

        Assert.Equal(fresh.StatusCode, duplicate.StatusCode);
        Assert.Equal(
            await fresh.Content.ReadAsStringAsync(),
            await duplicate.Content.ReadAsStringAsync());

        // And the second attempt did not take the account over.
        var user = await _host.UserAsync(taken);
        Assert.Equal("Dana Al-Otaibi", user.DisplayName);
    }

    [Fact]
    public async Task A_reset_for_an_unknown_address_looks_exactly_like_one_for_a_known_address()
    {
        var known = "auth-known@example.com";
        var (_, _) = await _host.SignedInCustomerAsync(known, "Mariam Al-Fahad", Password);

        var client = _host.Client();
        var hit = await client.PostAsJsonAsync("/api/account/forgot", new { email = known, language = "en" });
        var miss = await client.PostAsJsonAsync("/api/account/forgot",
            new { email = "auth-nobody-at-all@example.com", language = "en" });

        Assert.Equal(hit.StatusCode, miss.StatusCode);
        Assert.Equal(await hit.Content.ReadAsStringAsync(), await miss.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_unconfirmed_account_cannot_sign_in()
    {
        var client = _host.Client();
        const string email = "auth-unconfirmed@example.com";

        await client.PostAsJsonAsync("/api/account/register",
            new { name = "Khalid Nasser", email, password = Password, language = "en" });

        var response = await client.PostAsJsonAsync("/api/account/signin", new { email, password = Password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("email_not_confirmed", await CodeAsync(response));
    }

    /// <summary>
    /// "Confirm your email first" is a fact about an account that exists, so a caller who
    /// could not produce the password is not told it.
    /// </summary>
    [Fact]
    public async Task A_wrong_password_on_an_unconfirmed_account_discloses_nothing()
    {
        var client = _host.Client();
        const string email = "auth-unconfirmed-wrong@example.com";

        await client.PostAsJsonAsync("/api/account/register",
            new { name = "Khalid Nasser", email, password = Password, language = "en" });

        var response = await client.PostAsJsonAsync("/api/account/signin", new { email, password = "not-the-one" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_credentials", await CodeAsync(response));
    }

    [Fact]
    public async Task A_confirmed_customer_signs_in_and_the_session_is_the_customer_cookie()
    {
        var (client, _) = await _host.SignedInCustomerAsync("auth-signin@example.com", "Latifa Al-Sabah", Password);

        var profile = await client.GetAsync("/api/me/profile");
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);

        var body = JsonDocument.Parse(await profile.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("auth-signin@example.com", body.GetProperty("email").GetString());
        Assert.Equal("Latifa Al-Sabah", body.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task A_signed_out_visitor_is_refused_the_account_endpoints()
    {
        var client = _host.Client();

        foreach (var path in new[] { "/api/me/profile", "/api/me/library", "/api/me/orders" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
    }

    /// <summary>AC-8: a staff login must not open a storefront session.</summary>
    [Fact]
    public async Task An_administrator_cannot_sign_in_as_a_customer()
    {
        const string email = "auth-admin@example.com";

        using (var scope = _host.Scope())
        {
            var users = _host.Service<UserManager<NexusUser>>(scope);
            var roles = _host.Service<RoleManager<IdentityRole>>(scope);

            foreach (var role in NexusRoles.All)
                if (!await roles.RoleExistsAsync(role)) await roles.CreateAsync(new IdentityRole(role));

            var admin = new NexusUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = "Administrator",
                CreatedAt = DateTimeOffset.UtcNow
            };
            Assert.True((await users.CreateAsync(admin, Password)).Succeeded);
            await users.AddToRoleAsync(admin, NexusRoles.Admin);
        }

        var client = _host.Client();
        var response = await client.PostAsJsonAsync("/api/account/signin", new { email, password = Password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("not_a_customer", await CodeAsync(response));

        // And no session was opened as a side effect of being refused.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me/profile")).StatusCode);
    }

    [Fact]
    public async Task Signing_out_ends_the_session()
    {
        var (client, _) = await _host.SignedInCustomerAsync("auth-signout@example.com", "Omar Bader", Password);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/account/signout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me/profile")).StatusCode);
    }

    [Fact]
    public async Task A_password_reset_token_changes_the_password_and_the_old_one_stops_working()
    {
        const string email = "auth-reset@example.com";
        const string replacement = "a-completely-different-one";
        await _host.SignedInCustomerAsync(email, "Hessa Al-Mutairi", Password);

        string token;
        using (var scope = _host.Scope())
        {
            var users = _host.Service<UserManager<NexusUser>>(scope);
            var user = await users.FindByEmailAsync(email);
            token = AccountTokens.Encode(await users.GeneratePasswordResetTokenAsync(user!));
        }

        var user2 = await _host.UserAsync(email);
        var client = _host.Client();

        var reset = await client.PostAsJsonAsync("/api/account/reset",
            new { userId = user2.Id, token, password = replacement });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        var stale = await client.PostAsJsonAsync("/api/account/signin", new { email, password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);

        var fresh = _host.Client();
        var ok = await fresh.PostAsJsonAsync("/api/account/signin", new { email, password = replacement });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task A_tampered_reset_token_is_refused_and_the_password_is_untouched()
    {
        const string email = "auth-reset-bad@example.com";
        await _host.SignedInCustomerAsync(email, "Fahad Al-Ajmi", Password);
        var user = await _host.UserAsync(email);

        var client = _host.Client();
        var response = await client.PostAsJsonAsync("/api/account/reset",
            new { userId = user.Id, token = "not-a-token", password = "something-else-entirely" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_token", await CodeAsync(response));

        var fresh = _host.Client();
        Assert.Equal(HttpStatusCode.OK,
            (await fresh.PostAsJsonAsync("/api/account/signin", new { email, password = Password })).StatusCode);
    }

    [Fact]
    public async Task A_password_under_the_minimum_is_refused_before_anything_is_created()
    {
        var client = _host.Client();
        const string email = "auth-weak@example.com";

        var response = await client.PostAsJsonAsync("/api/account/register",
            new { name = "Short Password", email, password = "abc", language = "en" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("weak_password", await CodeAsync(response));

        using var scope = _host.Scope();
        Assert.Null(await _host.Service<UserManager<NexusUser>>(scope).FindByEmailAsync(email));
    }

    /// <summary>
    /// The "auth" policy permits eight requests per five minutes per address. A policy
    /// that is named but permits everything reads as protection in a review and is none,
    /// so the limit is exercised rather than assumed.
    /// </summary>
    [Fact]
    public async Task Sign_in_is_rate_limited()
    {
        var client = _host.Client("198.51.100.77");
        var codes = new List<HttpStatusCode>();

        for (var i = 0; i < 12; i++)
        {
            var response = await client.PostAsJsonAsync("/api/account/signin",
                new { email = "auth-flood@example.com", password = "wrong-but-long-enough" });
            codes.Add(response.StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
        Assert.Equal(HttpStatusCode.Unauthorized, codes[0]);
    }

    [Fact]
    public async Task Registration_is_rate_limited()
    {
        var client = _host.Client("198.51.100.78");
        var codes = new List<HttpStatusCode>();

        for (var i = 0; i < 12; i++)
        {
            var response = await client.PostAsJsonAsync("/api/account/register",
                new { name = "Flood Test", email = $"auth-flood-{i}@example.com", password = Password, language = "en" });
            codes.Add(response.StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
    }

    [Fact]
    public async Task Password_reset_requests_are_rate_limited()
    {
        var client = _host.Client("198.51.100.79");
        var codes = new List<HttpStatusCode>();

        for (var i = 0; i < 12; i++)
        {
            var response = await client.PostAsJsonAsync("/api/account/forgot",
                new { email = "auth-flood-reset@example.com", language = "en" });
            codes.Add(response.StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.GetProperty("code").GetString();
    }
}
