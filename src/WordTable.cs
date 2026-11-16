using System;
using System.Collections.Generic;

namespace ContentPacks
{
    // The word table, parsed the way the game reads it: every ':' removed from a row, rows split
    // on commas and padded to the header's width, and columns found by their header names.
    internal sealed class WordTable
    {
        // The header row as written, and split into names.
        internal readonly string HeaderLine;
        internal readonly string[] Header;
        internal readonly WordTableColumns Columns;
        // Every non-empty row after the header, in order.
        internal readonly List<string[]> Rows;
        // The line ending the table uses.
        internal readonly string Newline;

        private WordTable(string headerLine, string[] header, WordTableColumns columns, List<string[]> rows, string newline)
        {
            HeaderLine = headerLine;
            Header = header;
            Columns = columns;
            Rows = rows;
            Newline = newline;
        }

        // The parsed table, or null with the reason it can't be parsed.
        internal static WordTable Parse(string text, out string problem)
        {
            problem = null;
            var lines = TextPatcher.SplitLines(text, out _);
            if (lines.Count == 0)
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
            for (var i = 1; i < lines.Count; i++)
            {
                var line = StripColons(lines[i]);
                if (line.Length == 0)
                {
                    continue;
                }
                var cells = line.Split(',');
                if (cells.Length < header.Length)
                {
                    var padded = new string[header.Length];
                    for (var c = 0; c < padded.Length; c++)
                    {
                        padded[c] = c < cells.Length ? cells[c] : "";
                    }
                    cells = padded;
                }
                rows.Add(cells);
            }
            return new WordTable(lines[0], header, columns, rows, text.Contains("\r\n") ? "\r\n" : "\n");
        }

        // The game removes every ':' from a row before reading it. Its own table puts one before
        // number IDs like 1e2, so that a spreadsheet keeps them as text.
        internal static string StripColons(string line) => line.Replace(":", "");
    }
}
