using Nexus.Infrastructure.Admin;

namespace Nexus.UnitTests.Admin;

/// <summary>
/// The CSV reader. The operator's file comes out of Excel, so these are exactly the
/// cases Excel produces: a BOM, CRLF, quoted commas, doubled quotes and a line break
/// inside a description.
/// </summary>
public sealed class AdminCsvReaderTests
{
    [Fact]
    public void A_plain_file_parses_into_rows_and_fields()
    {
        var rows = CsvReader.Parse("a,b,c\r\n1,2,3\r\n");

        Assert.Equal(2, rows.Count);
        Assert.Equal(["a", "b", "c"], rows[0].Fields);
        Assert.Equal(["1", "2", "3"], rows[1].Fields);
    }

    [Fact]
    public void The_excel_byte_order_mark_does_not_become_part_of_the_first_header()
    {
        var rows = CsvReader.Parse("﻿category_slug,price_kwd\r\ninvesting,6.500\r\n");

        Assert.Equal("category_slug", rows[0].Fields[0]);
    }

    [Fact]
    public void A_quoted_comma_stays_inside_its_field()
    {
        var rows = CsvReader.Parse("t,d\r\nTitle,\"A guide, written for Kuwait\"\r\n");

        Assert.Equal("A guide, written for Kuwait", rows[1].Fields[1]);
    }

    [Fact]
    public void A_doubled_quote_is_one_quote()
    {
        var rows = CsvReader.Parse("d\r\n\"She said \"\"no\"\".\"\r\n");

        Assert.Equal("She said \"no\".", rows[1].Fields[0]);
    }

    /// <summary>
    /// A description with a paragraph break is one field on one row, and the line number
    /// of everything after it must still be the line the operator sees in their editor.
    /// </summary>
    [Fact]
    public void A_newline_inside_quotes_does_not_end_the_row_but_does_move_the_line_count()
    {
        var rows = CsvReader.Parse("t,d\r\nTitle,\"first\nsecond\"\r\nNext,Plain\r\n");

        Assert.Equal(3, rows.Count);
        Assert.Equal("first\nsecond", rows[1].Fields[1]);
        Assert.Equal(2, rows[1].Line);
        Assert.Equal(4, rows[2].Line);
    }

    [Fact]
    public void A_trailing_newline_is_not_an_empty_row()
    {
        Assert.Single(CsvReader.Parse("a,b\r\n"));
        Assert.Single(CsvReader.Parse("a,b\n"));
        Assert.Single(CsvReader.Parse("a,b"));
    }

    [Fact]
    public void Blank_lines_between_rows_are_skipped()
    {
        var rows = CsvReader.Parse("a\r\n\r\nb\r\n\r\n\r\nc\r\n");

        Assert.Equal(3, rows.Count);
        Assert.Equal("c", rows[2].Fields[0]);
    }

    [Fact]
    public void An_empty_file_yields_no_rows()
    {
        Assert.Empty(CsvReader.Parse(""));
    }

    [Fact]
    public void Arabic_survives_the_parse_unchanged()
    {
        const string arabic = "المحفظة الصبور";
        var rows = CsvReader.Parse("t\r\n" + arabic + "\r\n");

        Assert.Equal(arabic, rows[1].Fields[0]);
    }

    [Fact]
    public void An_empty_trailing_field_is_still_a_field()
    {
        var rows = CsvReader.Parse("a,b,c\r\n1,2,\r\n");

        Assert.Equal(3, rows[1].Fields.Count);
        Assert.Equal("", rows[1].Fields[2]);
    }
}
