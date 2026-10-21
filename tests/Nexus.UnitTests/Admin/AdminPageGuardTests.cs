using Microsoft.AspNetCore.Http;
using Nexus.Infrastructure.Admin;

namespace Nexus.UnitTests.Admin;

/// <summary>
/// Which paths the page guard stands in front of.
/// <para>
/// The data is already behind the policies; this exists so an anonymous visitor cannot
/// read the panel's pages and take a complete map of the API, the field names and the
/// actions off a screen nobody thought carried anything.
/// </para>
/// </summary>
public sealed class AdminPageGuardTests
{
    [Theory]
    [InlineData("/admin")]
    [InlineData("/admin/")]
    [InlineData("/admin/index.html")]
    [InlineData("/admin/books.html")]
    [InlineData("/ADMIN/Settings.html")]
    public void An_admin_page_is_guarded(string path)
    {
        Assert.True(AdminPageGuard.IsProtectedPage(new PathString(path)));
    }

    /// <summary>
    /// The API is excluded because the controllers carry the real policies — Admin versus
    /// Staff, which the guard cannot know — and a blanket redirect in front of them would
    /// turn a 403 into a login page an XHR cannot read.
    /// </summary>
    [Theory]
    [InlineData("/admin/api/session")]
    [InlineData("/admin/api/books")]
    public void The_admin_api_passes_through_to_its_own_policies(string path)
    {
        Assert.False(AdminPageGuard.IsProtectedPage(new PathString(path)));
    }

    [Fact]
    public void The_login_page_is_reachable_or_nobody_could_ever_sign_in()
    {
        Assert.False(AdminPageGuard.IsProtectedPage(new PathString("/admin/login.html")));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/shop.html")]
    [InlineData("/assets/js/site.js")]
    [InlineData("/api/books")]
    [InlineData("/administrator.html")]   // not a segment match, and not ours
    public void The_storefront_is_untouched(string path)
    {
        Assert.False(AdminPageGuard.IsProtectedPage(new PathString(path)));
    }
}
