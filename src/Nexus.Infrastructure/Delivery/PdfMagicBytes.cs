
namespace Nexus.Infrastructure.Delivery;

/// <summary>Why a candidate file is not a PDF we can sell, or <see cref="Ok"/>.</summary>
public enum PdfShapeVerdict
{
    Ok = 0,
    BadMagic = 1,
    Truncated = 2,
    Empty = 3
}

/// <summary>
/// Structural sniffing of a file that is already on disk. Nothing here trusts the
/// filename, the extension or the declared content type — those are a claim made by the
/// client, and a claim is not evidence.
/// </summary>
public static class PdfMagicBytes
{
    /// <summary>The spec tolerates a preamble, and several real generators emit one.</summary>
    public const int HeaderWindow = 1024;

    /// <summary>A trailer can be followed by junk; it cannot be a long way from the end.</summary>
    public const int TrailerWindow = 2048;

    private static readonly byte[] Header = "%PDF-"u8.ToArray();
    private static readonly byte[] Trailer = "%%EOF"u8.ToArray();

    public static PdfShapeVerdict Inspect(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, long totalLength)
    {
        if (totalLength == 0) return PdfShapeVerdict.Empty;

        var at = IndexOf(head[..Math.Min(head.Length, HeaderWindow)], Header);
        if (at < 0) return PdfShapeVerdict.BadMagic;

        // "%PDF-" followed by a version: "1." or "2.". A file that merely contains the
        // string somewhere in a preamble is not a PDF.
        var version = head[(at + Header.Length)..];
        if (version.Length < 2 || version[1] != (byte)'.' || (version[0] != (byte)'1' && version[0] != (byte)'2'))
            return PdfShapeVerdict.BadMagic;

        // This is what catches a truncated upload, which passes the magic-byte check
        // perfectly and then fails to open on the buyer's phone three weeks later.
        return IndexOf(tail, Trailer) < 0 ? PdfShapeVerdict.Truncated : PdfShapeVerdict.Ok;
    }

    /// <summary>Reads only the two windows it needs; the file is never loaded into memory.</summary>
    public static async Task<PdfShapeVerdict> InspectFileAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);

        var length = fs.Length;
        if (length == 0) return PdfShapeVerdict.Empty;

        var head = new byte[(int)Math.Min(HeaderWindow + 8, length)];
        await fs.ReadExactlyAsync(head, ct);

        var tailLength = (int)Math.Min(TrailerWindow, length);
        fs.Seek(-tailLength, SeekOrigin.End);
        var tail = new byte[tailLength];
        await fs.ReadExactlyAsync(tail, ct);

        return Inspect(head, tail, length);
    }

    public static string Code(PdfShapeVerdict verdict) => verdict switch
    {
        PdfShapeVerdict.BadMagic => "pdf_bad_magic",
        PdfShapeVerdict.Truncated => "pdf_truncated",
        PdfShapeVerdict.Empty => "pdf_bad_magic",
        _ => ""
    };

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return -1;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle)) return i;
        }
        return -1;
    }
}
