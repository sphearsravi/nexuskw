using System.Text;
using System.Text.RegularExpressions;

namespace Nexus.Infrastructure.Delivery;

/// <summary>
/// Everything that turns an identifier into a path on disk, in one place.
/// <para>
/// The library layout is content-addressed — <c>{bookId}/{sha256}.pdf</c> — so no
/// operator- or customer-supplied text ever reaches a path and traversal is impossible
/// by construction. Two further checks are still applied here, because "impossible by
/// construction" survives exactly one careless refactor: the key must match
/// <see cref="KeyPattern"/>, and the resolved absolute path must still sit under the
/// root after <see cref="Path.GetFullPath(string)"/>.
/// </para>
/// </summary>
public sealed partial class LibraryPaths
{
    [GeneratedRegex(@"^[1-9][0-9]{0,8}/[0-9a-f]{64}\.pdf$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"^[0-9a-f]{2}/[1-9][0-9]{0,8}-[0-9a-f]{16}\.pdf$")]
    private static partial Regex StampedKeyPattern();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();

    public LibraryPaths(string root)
    {
        Root = Path.GetFullPath(root);
        Library = Path.Combine(Root, "library");
        Incoming = Path.Combine(Root, "incoming");
        Stamped = Path.Combine(Root, "stamped");
    }

    public string Root { get; }
    public string Library { get; }
    public string Incoming { get; }
    public string Stamped { get; }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Library);
        Directory.CreateDirectory(Incoming);
        Directory.CreateDirectory(Stamped);
    }

    public static string KeyFor(int bookId, string sha256) => $"{bookId}/{sha256}.pdf";

    public static bool IsValidKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    /// <summary>
    /// Absolute path for a library key, or null when the key is not one we minted. A
    /// rejected key never touches the filesystem.
    /// </summary>
    public string? ResolveLibrary(string? storageKey)
    {
        if (!IsValidKey(storageKey)) return null;
        return Contained(Library, storageKey!);
    }

    /// <summary>
    /// The cached stamped copy for one grant, keyed on the grant id and the source hash.
    /// <para>
    /// The grant id, not the token: a token in a filename is a bearer credential in
    /// directory listings, backups and every stack trace that prints a path. The source
    /// hash, because an operator who uploads a corrected PDF must not keep serving the
    /// old one to everyone who already downloaded once.
    /// </para>
    /// </summary>
    public static string StampedKeyFor(int grantId, string sourceSha256) =>
        $"{grantId % 256:x2}/{grantId}-{sourceSha256[..16]}.pdf";

    public string? ResolveStamped(string stampedKey) =>
        StampedKeyPattern().IsMatch(stampedKey) ? Contained(Stamped, stampedKey) : null;

    private static string? Contained(string baseDir, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(baseDir, relative));
        var prefix = baseDir.EndsWith(Path.DirectorySeparatorChar)
            ? baseDir
            : baseDir + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>
    /// A filename-safe slug. An admin-supplied title containing CR/LF would otherwise
    /// put a header injection — or at best a 500 — behind a download that has already
    /// been counted.
    /// </summary>
    public static string Slug(string? text, int bookId)
    {
        if (string.IsNullOrWhiteSpace(text)) return $"book-{bookId}";

        var lowered = text.ToLowerInvariant();
        var ascii = new StringBuilder(lowered.Length);
        foreach (var ch in lowered) ascii.Append(ch < 128 ? ch : ' ');

        var slug = NonSlug().Replace(ascii.ToString(), "-").Trim('-');
        if (slug.Length > 60) slug = slug[..60].TrimEnd('-');
        return slug.Length == 0 ? $"book-{bookId}" : slug;
    }
}
