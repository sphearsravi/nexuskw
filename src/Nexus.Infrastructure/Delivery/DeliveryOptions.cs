namespace Nexus.Infrastructure.Delivery;

/// <summary>
/// Bound from the <c>Nexus:Library</c> configuration section. Everything here is
/// deployment shape, not operator policy: the things an operator changes without a
/// deployment live in <c>Settings</c> and are read through <c>ISettingsReader</c>.
/// </summary>
public sealed class DeliveryOptions
{
    public const string Section = "Nexus:Library";

    /// <summary>
    /// Absolute path to the <c>App_Data</c> root. Empty means
    /// <c>{ContentRootPath}/App_Data</c>; a server with a separate data volume sets it.
    /// </summary>
    public string Root { get; set; } = "";

    /// <summary>
    /// How long a download waits for the stamping semaphore before giving up. A wait is
    /// cheaper than an OOM on a 512 MB pool shared with five other sites, but a buyer
    /// staring at a spinner is not, so the wait is bounded and the refusal costs them
    /// nothing.
    /// </summary>
    public int StampQueueTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Absolute path to a TrueType file used for the watermark. Empty falls back to the
    /// first usable font found in the platform's font directories; none at all means the
    /// source is served unstamped rather than not at all.
    /// </summary>
    public string StampFontPath { get; set; } = "";
}
