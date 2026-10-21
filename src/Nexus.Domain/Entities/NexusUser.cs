using Microsoft.AspNetCore.Identity;

namespace Nexus.Domain.Entities;

/// <summary>
/// A login. One Identity store serves all three audiences, separated by role:
/// <c>Customer</c>, <c>Staff</c> and <c>Admin</c>.
/// <para>
/// A customer login points at a <see cref="Entities.Customer"/> row, which is where
/// orders hang. The two are deliberately separate: a guest checkout creates a Customer
/// with no login at all, and registering later claims it.
/// </para>
/// </summary>
public class NexusUser : IdentityUser
{
    /// <summary>Null for an administrator, who buys nothing.</summary>
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>Snapshot of the language they last chose, for emails sent outside a request.</summary>
    public string PreferredLanguage { get; set; } = "en";

    public DateTimeOffset CreatedAt { get; set; }
}

public static class NexusRoles
{
    public const string Admin = "Admin";
    public const string Staff = "Staff";
    public const string Customer = "Customer";

    public static readonly string[] All = [Admin, Staff, Customer];
}

/// <summary>
/// Two cookies, never one.
/// <para>
/// The blast radius differs: a stolen customer session loses one reader's books, a
/// stolen admin session loses the catalogue, the order history and every customer's
/// email address. Sharing a cookie between them would make the cheaper credential a
/// route to the dearer one.
/// </para>
/// </summary>
public static class NexusAuth
{
    public const string CustomerScheme = "nexus_customer";
    public const string AdminScheme = "nexus_admin";

    public const string AdminOnly = "AdminOnly";
    public const string AdminOrStaff = "AdminOrStaff";
    public const string CustomerOnly = "CustomerOnly";
}
