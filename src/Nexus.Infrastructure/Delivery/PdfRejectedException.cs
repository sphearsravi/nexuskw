namespace Nexus.Infrastructure.Delivery;

/// <summary>
/// An upload the library refuses to take. <see cref="Code"/> is the stable wire code the
/// admin UI maps to a message — never prose, so the storefront owns every string a human
/// reads and the API invents none.
/// </summary>
public sealed class PdfRejectedException(string code, string reason)
    : Exception($"{code}: {reason}")
{
    public string Code { get; } = code;
}
