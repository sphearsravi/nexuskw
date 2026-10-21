using System.Text;

namespace Nexus.Infrastructure.Admin;

/// <summary>A parsed row and the 1-based line of the file it started on.</summary>
public sealed record CsvRow(int Line, IReadOnlyList<string> Fields);

/// <summary>
/// A small RFC 4180 reader.
/// <para>
/// Hand-written rather than taken from a package because the whole job is five rules —
/// quotes, doubled quotes inside quotes, embedded commas, embedded newlines, CRLF — and
/// the operator's file will come out of Excel, which obeys exactly those. The line number
/// is carried because "row 23 is wrong" is the only useful thing to tell someone holding
/// a spreadsheet.
/// </para>
/// </summary>
public static class CsvReader
{
    public static List<CsvRow> Parse(string text)
    {
        var rows = new List<CsvRow>();
        if (string.IsNullOrEmpty(text)) return rows;

        // Excel writes a UTF-8 BOM, and a BOM glued to the first header name turns
        // "category_slug" into a column nobody can match.
        if (text[0] == '﻿') text = text[1..];

        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var rowStartedOn = 1;
        var rowHasContent = false;

        void EndField()
        {
            fields.Add(field.ToString());
            field.Clear();
        }

        void EndRow()
        {
            EndField();
            // A trailing newline, or a run of blank lines, is not a row of empty values.
            if (rowHasContent) rows.Add(new CsvRow(rowStartedOn, fields.ToArray()));
            fields.Clear();
            rowHasContent = false;
            rowStartedOn = line;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else
                {
                    if (c == '\n') line++;
                    field.Append(c);
                }
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    rowHasContent = true;
                    break;
                case ',':
                    EndField();
                    rowHasContent = true;
                    break;
                case '\r':
                    break;   // CRLF: the LF does the work
                case '\n':
                    line++;
                    EndRow();
                    break;
                default:
                    field.Append(c);
                    if (!char.IsWhiteSpace(c)) rowHasContent = true;
                    break;
            }
        }

        if (rowHasContent || field.Length > 0 || fields.Count > 0) EndRow();

        return rows;
    }
}
