using System;
using System.Collections.Generic;

namespace ContentPacks
{
    // The word table, parsed the way the game reads it: lines split on CR and LF with empty ones
    // dropped, every ':' removed from a row, rows split on commas, and columns found by their
    // exact header names.
    internal sealed class WordTable
    {
        // The header row as written, and split into names.
        internal readonly string HeaderLine;
        internal readonly string[] Header;
        internal readonly WordTableColumns Columns;
        // Every row after the header, padded to the header's width, in order. A row shorter than
        // the header, which stops the game while it loads, is in Short too.
        internal readonly List<string[]> Rows;
        internal readonly HashSet<string[]> Short;
        // The line ending the table uses.
        internal readonly string Newline;

        private WordTable(string headerLine, string[] header, WordTableColumns columns, List<string[]> rows, HashSet<string[]> shortRows, string newline)
        {
            HeaderLine = headerLine;
            Header = header;
            Columns = columns;
            Rows = rows;
            Short = shortRows;
            Newline = newline;
        }

        // The parsed table, or null with the reason it can't be parsed.
        internal static WordTable Parse(string text, out string problem)
        {
            problem = null;
            var lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
            {
                problem = "the word table is empty";
                return null;
            }
            var header = lines[0].Split(',');
            var columns = WordTableColumns.From(header, out var missing);
            if (columns == null)
            {
                problem = $"the word table has no {missing} column";
                return null;
            }
            var rows = new List<string[]>();
            var shortRows = new HashSet<string[]>();
            for (var i = 1; i < lines.Length; i++)
            {
                var cells = StripColons(lines[i]).Split(',');
                var isShort = cells.Length < header.Length;
                if (isShort)
                {
                    var padded = new string[header.Length];
                    for (var c = 0; c < padded.Length; c++)
                    {
                        padded[c] = c < cells.Length ? cells[c] : "";
                    }
                    cells = padded;
                    shortRows.Add(cells);
                }
                rows.Add(cells);
            }
            return new WordTable(lines[0], header, columns, rows, shortRows, text.Contains("\r\n") ? "\r\n" : "\n");
        }

        // The game removes every ':' from a row before reading it. Its own table puts one before
        // number IDs like 1e2, so that a spreadsheet keeps them as text.
        internal static string StripColons(string line) => line.Replace(":", "");
    }
}
