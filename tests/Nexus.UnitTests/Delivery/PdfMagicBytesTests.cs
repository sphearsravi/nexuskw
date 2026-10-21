using System.Text;
using Nexus.Infrastructure.Delivery;

namespace Nexus.UnitTests.Delivery;

/// <summary>
/// Uploads are validated by what is in the bytes, never by the extension or the declared
/// content type — those are a claim made by whoever is uploading.
/// </summary>
public sealed class PdfMagicBytesTests
{
    private static byte[] Head(string text) => Encoding.ASCII.GetBytes(text);

    private static PdfShapeVerdict Inspect(string head, string tail) =>
        PdfMagicBytes.Inspect(Head(head), Head(tail), head.Length + tail.Length);

    [Fact]
    public void A_header_at_offset_zero_is_accepted() =>
        Assert.Equal(PdfShapeVerdict.Ok, Inspect("%PDF-1.7\n...", "...\n%%EOF\n"));

    // Not "at offset 0": the spec tolerates a preamble and several real generators emit
    // one, so a strict offset check would reject legitimate files.
    [Fact]
    public void A_header_after_a_preamble_is_accepted() =>
        Assert.Equal(PdfShapeVerdict.Ok, Inspect(new string('x', 600) + "%PDF-2.0", "%%EOF"));

    [Fact]
    public void A_header_beyond_the_window_is_rejected() =>
        Assert.Equal(PdfShapeVerdict.BadMagic, Inspect(new string('x', 1100) + "%PDF-1.4", "%%EOF"));

    [Fact]
    public void A_png_renamed_to_pdf_is_rejected() =>
        Assert.Equal(PdfShapeVerdict.BadMagic, Inspect("\u0089PNG\r\n\u001a\n", "IEND"));

    // "%PDF-" with no version is a file that merely contains the string.
    [Fact]
    public void A_header_without_a_version_is_rejected() =>
        Assert.Equal(PdfShapeVerdict.BadMagic, Inspect("%PDF-abc", "%%EOF"));

    [Fact]
    public void An_empty_file_is_rejected() =>
        Assert.Equal(PdfShapeVerdict.Empty, PdfMagicBytes.Inspect([], [], 0));

    /// <summary>
    /// The case the trailer check exists for: a truncated upload passes the magic-byte
    /// check perfectly and then fails to open on the buyer's phone three weeks later.
    /// </summary>
    [Fact]
    public void A_truncated_file_is_rejected() =>
        Assert.Equal(PdfShapeVerdict.Truncated, Inspect("%PDF-1.7\nbody", "more body, no end"));

    [Fact]
    public void A_trailer_at_the_very_last_byte_is_accepted() =>
        Assert.Equal(PdfShapeVerdict.Ok, Inspect("%PDF-1.5", "%%EOF"));

    [Fact]
    public void A_trailer_followed_by_junk_inside_the_window_is_accepted() =>
        Assert.Equal(PdfShapeVerdict.Ok, Inspect("%PDF-1.5", "%%EOF" + new string('z', 1000)));

    [Fact]
    public void The_wire_code_names_the_problem()
    {
        Assert.Equal("pdf_bad_magic", PdfMagicBytes.Code(PdfShapeVerdict.BadMagic));
        Assert.Equal("pdf_truncated", PdfMagicBytes.Code(PdfShapeVerdict.Truncated));
        Assert.Equal("", PdfMagicBytes.Code(PdfShapeVerdict.Ok));
    }
}
