using PdfSharp.Fonts;

namespace Nexus.Infrastructure.Delivery;

/// <summary>
/// Supplies the one font the watermark needs.
/// <para>
/// PDFsharp 6 has no fonts of its own: without a resolver every stamping test passes on
/// a developer's Windows box and fails on a build agent, which is the worst shape a
/// failure can take. This looks for a usable TrueType file — a configured one first,
/// then the platform's font directories — and reports honestly when there is none, so
/// the caller can serve the book unstamped instead of not at all.
/// </para>
/// </summary>
public sealed class StampFontResolver : IFontResolver
{
    public const string FamilyName = "NexusStamp";
    private const string FaceName = "NexusStamp#Regular";

    private static readonly string[] Candidates =
    [
        @"C:\Windows\Fonts\arial.ttf",
        @"C:\Windows\Fonts\segoeui.ttf",
        @"C:\Windows\Fonts\tahoma.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
        "/Library/Fonts/Arial.ttf",
        "/System/Library/Fonts/Supplemental/Arial.ttf"
    ];

    private readonly byte[]? _face;

    public StampFontResolver(string? configuredPath)
    {
        var path = Find(configuredPath);
        _face = path is null ? null : File.ReadAllBytes(path);
        ResolvedPath = path;
    }

    /// <summary>The file in use, or null when nothing usable was found.</summary>
    public string? ResolvedPath { get; }

    public bool HasFont => _face is not null;

    public byte[]? GetFont(string faceName) =>
        faceName == FaceName ? _face : null;

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        _face is null ? null : new FontResolverInfo(FaceName);

    private static string? Find(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        foreach (var candidate in Candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
