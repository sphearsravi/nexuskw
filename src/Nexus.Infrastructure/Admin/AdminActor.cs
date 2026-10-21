using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Nexus.Infrastructure.Admin;

/// <summary>Who is making an administrative change, as the audit trail will record them.</summary>
public sealed record AdminActor(string? Id, string? Email, string? IpAddress)
{
    public static readonly AdminActor Unknown = new(null, null, null);
}

/// <summary>
/// Resolves the acting administrator from the current request.
/// <para>
/// A service, not a parameter threaded through every method: an audit row written without
/// an actor is worse than no audit row, because it looks like a record and answers nothing.
/// Making it ambient means a new endpoint cannot forget to pass it.
/// </para>
/// </summary>
public interface IAdminActorAccessor
{
    AdminActor Current { get; }
}

public sealed class HttpAdminActorAccessor(IHttpContextAccessor http) : IAdminActorAccessor
{
    public AdminActor Current
    {
        get
        {
            var ctx = http.HttpContext;
            if (ctx is null) return AdminActor.Unknown;

            var user = ctx.User;
            return new AdminActor(
                user.FindFirstValue(ClaimTypes.NameIdentifier),
                user.FindFirstValue(ClaimTypes.Email) ?? user.Identity?.Name,
                ctx.Connection.RemoteIpAddress?.ToString());
        }
    }
}
