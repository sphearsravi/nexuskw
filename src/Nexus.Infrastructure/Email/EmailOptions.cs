namespace Nexus.Infrastructure.Email;

public enum EmailTransport
{
    /// <summary>Nothing is sent and no socket is opened. The row is still written, Suppressed.</summary>
    None = 0,
    /// <summary>A real RFC 5322 <c>.eml</c> on disk, so the Arabic can be opened in a mail client.</summary>
    FileDrop = 1,
    Smtp = 2
}

/// <summary>
/// Transport configuration. <b>Credentials live here and never in the Settings table</b>:
/// the operator edits content and policy from a screen, the server owner sets transport
/// from App Pool environment variables, and neither is committed.
/// </summary>
public sealed class EmailOptions
{
    public const string Section = "Email";

    /// <summary>
    /// Defaults to <see cref="EmailTransport.None"/>. That is the correct state until an
    /// SMTP provider is chosen: the store sells and delivers, and the operator sees a
    /// Suppressed row per customer rather than silence.
    /// </summary>
    public EmailTransport Transport { get; set; } = EmailTransport.None;

    public FileDropOptions FileDrop { get; set; } = new();
    public SmtpOptions Smtp { get; set; } = new();

    /// <summary>
    /// The organisational domain of the envelope sender. DMARC aligns SPF on MAIL FROM,
    /// so this must be the <c>From:</c> domain or a subdomain of it or every message is
    /// quarantined — see <see cref="VerpAddress"/>, which refuses to use it otherwise.
    /// </summary>
    public string BounceDomain { get; set; } = "";

    /// <summary>Seconds between idle dispatcher passes.</summary>
    public int DispatchIntervalSeconds { get; set; } = 15;

    /// <summary>Rows claimed per pass. Sends are serial; this bounds the lease, not concurrency.</summary>
    public int BatchSize { get; set; } = 10;

    /// <summary>
    /// How long a claimed row stays <c>Sending</c> before another dispatcher may recover
    /// it. Comfortably longer than the SMTP timeout.
    /// </summary>
    public int LeaseMinutes { get; set; } = 2;

    /// <summary>
    /// Problems that make the configured transport unusable. Empty means usable.
    /// <para>
    /// This degrades; it never throws. Refusing to start would take the store down over
    /// email, and the store can still sell and deliver without it. What must not happen is
    /// silence, and Suppressed rows plus a red health check are not silence.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (Transport == EmailTransport.Smtp)
        {
            if (string.IsNullOrWhiteSpace(Smtp.Host))
                problems.Add("Email:Smtp:Host is empty while Email:Transport is Smtp.");
            if (Smtp.Port is < 1 or > 65535)
                problems.Add($"Email:Smtp:Port {Smtp.Port} is not a port number.");
        }

        if (Transport == EmailTransport.FileDrop && string.IsNullOrWhiteSpace(FileDrop.Path))
            problems.Add("Email:FileDrop:Path is empty while Email:Transport is FileDrop.");

        return problems;
    }

    /// <summary>The transport actually used: a misconfigured one degrades to <c>None</c>.</summary>
    public EmailTransport EffectiveTransport() => Problems().Count == 0 ? Transport : EmailTransport.None;
}

public sealed class FileDropOptions
{
    /// <summary>
    /// Server-owned and never web-served. Swept at seven days by the nightly pass, because
    /// these files do contain live download links.
    /// </summary>
    public string Path { get; set; } = System.IO.Path.Combine("App_Data", "mail-drop");
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;
    public string User { get; set; } = "";

    /// <summary>App Pool environment variable <c>Email__Smtp__Password</c>. Never a committed value.</summary>
    public string Password { get; set; } = "";

    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>
/// Where the store lives, for the absolute URLs in an email.
/// <para>
/// Never built from the request: the dispatcher runs on a background thread with no
/// <c>HttpContext</c>, so an <c>IUrlHelper</c> there yields null and ships a relative path
/// — a dead link in a mail client that looks perfectly fine in any test that renders the
/// body to a string.
/// </para>
/// </summary>
public sealed class SiteOptions
{
    public const string Section = "Site";

    public string PublicBaseUrl { get; set; } = "";

    public IReadOnlyList<string> Problems() =>
        Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? []
            : [$"Site:PublicBaseUrl ('{PublicBaseUrl}') is not an absolute http(s) URL; emailed links would be relative."];

    /// <summary>Base URL with any trailing slash removed, so concatenation cannot double it.</summary>
    public string BaseOrFallback() =>
        (string.IsNullOrWhiteSpace(PublicBaseUrl) ? "https://localhost:5091" : PublicBaseUrl).TrimEnd('/');
}
