using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Domain.Entities;

namespace Nexus.IntegrationTests;

/// <summary>
/// Who may open the control panel, and — mostly — who may not.
/// <para>
/// Every authorisation rule here is asserted by its refusal. A test that only proves an
/// administrator can reach a screen says nothing about whether anybody else can.
/// </para>
/// </summary>
[Collection(NexusCollection.Name)]
public sealed class AdminAuthTestsP7(NexusApp app) : IDisposable
{
    private readonly AdminHostP7 _host = new(app);

    public void Dispose() => _host.Dispose();

    /// <summary>Every write the panel can make, as a method and a path.</summary>
    public static TheoryData<string, string> AdminOnlyWrites() => new()
    {
        { "DELETE", "/admin/api/books/1" },
        { "DELETE", "/admin/api/categories/investing" },
        { "DELETE", "/admin/api/customers/1" },
        { "POST", "/admin/api/orders/1/refund" },
        { "PUT", "/admin/api/settings/delivery.maxDownloads" },
        { "GET", "/admin/api/settings" },
        { "GET", "/admin/api/audit" }
    };

    public static TheoryData<string> StaffReadable() => new()
    {
        "/admin/api/dashboard",
        "/admin/api/books",
        "/admin/api/categories",
        "/admin/api/orders",
        "/admin/api/customers",
        "/admin/api/downloads"
    };

    [Theory]
    [MemberData(nameof(StaffReadable))]
    public async Task An_unauthenticated_request_gets_a_status_code_not_a_page(string url)
    {
        var response = await _host.Anonymous().GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("<html", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(StaffReadable))]
    public async Task A_customer_cookie_does_not_open_the_admin_api(string url)
    {
        var response = await _host.AsCustomer().GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The gate is the cookie, not the role claim inside it. A forged or stolen customer
    /// session carrying an Admin role must still open nothing, because the admin policies
    /// only ever read the admin scheme.
    /// </summary>
    [Fact]
    public async Task An_admin_role_on_the_customer_cookie_still_opens_nothing()
    {
        var response = await _host.AsAdminOnTheWrongCookie().GetAsync("/admin/api/books");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(StaffReadable))]
    public async Task Staff_can_read_the_operational_screens(string url)
    {
        var response = await _host.AsStaff().GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(AdminOnlyWrites))]
    public async Task Staff_are_refused_the_admin_only_actions(string method, string url)
    {
        var client = _host.AsStaff();
        var response = await client.SendAsync(new HttpMethod(method), url,
            method == "GET" ? null : new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        // 403, not 404: the account is known and the action is not theirs. A 404 here
        // would also be a refusal, but it would hide an authorisation bug behind a
        // missing row the day the row exists.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_administrator_reaches_the_admin_only_screens()
    {
        var response = await _host.AsAdmin().GetAsync("/admin/api/settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Re-sending an order's links is Staff work. Re-sending them <i>somewhere else</i>
    /// is how a borrowed staff account walks out with the catalogue, so it is Admin.
    /// </summary>
    [Fact]
    public async Task Redirecting_a_resend_to_another_address_is_admin_only()
    {
        var staff = await _host.AsStaff().PostJsonAsync(
            "/admin/api/orders/1/resend-email", new { to = "somewhere.else@example.com" });

        Assert.Equal(HttpStatusCode.Forbidden, staff.StatusCode);

        var admin = await _host.AsAdmin().PostJsonAsync(
            "/admin/api/orders/1/resend-email", new { to = "somewhere.else@example.com" });

        Assert.NotEqual(HttpStatusCode.Forbidden, admin.StatusCode);
    }

    [Fact]
    public async Task A_write_without_the_antiforgery_token_is_refused()
    {
        var client = _host.AsAdmin();
        client.SkipAntiforgery = true;

        var response = await client.PostJsonAsync("/admin/api/books/1/unpublish", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- the static pages --------------------------------------------------

    [Fact]
    public async Task An_anonymous_visitor_is_sent_to_the_login_page()
    {
        var response = await _host.Anonymous()
            .SendAsync(HttpMethod.Get, "/admin/index.html", null, "text/html");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin/login.html", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task The_login_page_itself_is_reachable()
    {
        var response = await _host.Anonymous()
            .SendAsync(HttpMethod.Get, "/admin/login.html", null, "text/html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_signed_in_operator_gets_the_page()
    {
        var response = await _host.AsStaff()
            .SendAsync(HttpMethod.Get, "/admin/index.html", null, "text/html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_storefront_is_untouched_by_the_guard()
    {
        var response = await _host.Anonymous()
            .SendAsync(HttpMethod.Get, "/index.html", null, "text/html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- signing in for real -----------------------------------------------

    private const string GoodPassword = "correct-horse-staple";
    private const string WrongPassword = "wrong-horse-staple";

    private async Task EnsureUserAsync(string email, string? role, bool confirmed = true)
    {
        using var scope = _host.Scope();
        var users = _host.Service<UserManager<NexusUser>>(scope);
        var roles = _host.Service<RoleManager<IdentityRole>>(scope);

        foreach (var name in NexusRoles.All)
            if (!await roles.RoleExistsAsync(name)) await roles.CreateAsync(new IdentityRole(name));

        if (await users.FindByEmailAsync(email) is not null) return;

        var user = new NexusUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = confirmed,
            DisplayName = email,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var created = await users.CreateAsync(user, GoodPassword);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));

        if (role is not null) await users.AddToRoleAsync(user, role);
    }

    [Fact]
    public async Task The_right_password_opens_a_session_on_the_admin_cookie()
    {
        await EnsureUserAsync("signin.admin@nexus.test", NexusRoles.Admin);

        var client = _host.Anonymous();
        var response = await client.PostJsonAsync("/admin/api/session",
            new { email = "signin.admin@nexus.test", password = GoodPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var cookies = response.Headers.TryGetValues("Set-Cookie", out var set) ? set.ToList() : [];
        Assert.Contains(cookies, c => c.StartsWith(NexusAuth.AdminScheme + "=", StringComparison.Ordinal));
        Assert.DoesNotContain(cookies, c => c.StartsWith(NexusAuth.CustomerScheme + "=", StringComparison.Ordinal));

        // The session the cookie opens is a working one.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/api/books")).StatusCode);
    }

    [Fact]
    public async Task The_wrong_password_is_refused_and_counts_towards_lockout()
    {
        await EnsureUserAsync("signin.badpass@nexus.test", NexusRoles.Admin);

        var response = await _host.Anonymous().PostJsonAsync("/admin/api/session",
            new { email = "signin.badpass@nexus.test", password = WrongPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using var scope = _host.Scope();
        var users = _host.Service<UserManager<NexusUser>>(scope);
        var user = await users.FindByEmailAsync("signin.badpass@nexus.test");
        Assert.Equal(1, await users.GetAccessFailedCountAsync(user!));
    }

    /// <summary>
    /// AC-8: a customer's credentials are a different thing from an operator's. The
    /// correct password on a Customer account must not open the panel.
    /// </summary>
    [Fact]
    public async Task A_customer_account_cannot_sign_into_the_panel()
    {
        await EnsureUserAsync("signin.reader@example.com", NexusRoles.Customer);

        var response = await _host.Anonymous().PostJsonAsync("/admin/api/session",
            new { email = "signin.reader@example.com", password = GoodPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var set) &&
                     set.Any(c => c.StartsWith(NexusAuth.AdminScheme + "=", StringComparison.Ordinal)));
    }

    /// <summary>RequireConfirmedEmail is on, and an unconfirmed address is one somebody else may hold.</summary>
    [Fact]
    public async Task An_unconfirmed_administrator_cannot_sign_in()
    {
        await EnsureUserAsync("signin.unconfirmed@nexus.test", NexusRoles.Admin, confirmed: false);

        var response = await _host.Anonymous().PostJsonAsync("/admin/api/session",
            new { email = "signin.unconfirmed@nexus.test", password = GoodPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_refusal_does_not_say_which_part_was_wrong()
    {
        await EnsureUserAsync("signin.oracle@nexus.test", NexusRoles.Admin);

        var known = await _host.Anonymous().PostJsonAsync("/admin/api/session",
            new { email = "signin.oracle@nexus.test", password = WrongPassword });
        var unknown = await _host.Anonymous().PostJsonAsync("/admin/api/session",
            new { email = "nobody.at.all@nexus.test", password = WrongPassword });

        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
    }
}
