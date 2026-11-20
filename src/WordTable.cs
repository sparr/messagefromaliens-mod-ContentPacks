using System;
using System.Collections.Generic;

namespace ContentPacks
{
    // The word table, parsed the way the game reads it: lines split on CR and LF with empty ones
    // dropped, every ':' removed from a row, rows split on commas, columns found by their exact
    // header names, rows with an empty word skipped, and a word's last row taking effect at the
    // place of its first.
    internal sealed class WordTable
    {
        // The header row as written, and split into names.
        internal readonly string HeaderLine;
        internal readonly string[] Header;
        internal readonly WordTableColumns Columns;
        // Every row after the header with a word, padded to the header's width, in order. A row
        // shorter than the header, which stops the game while it loads, is in Short too, and is
        // kept even when its word is empty.
        internal readonly List<string[]> Rows;
        internal readonly HashSet<string[]> Short;
        // The line ending the table uses.
        internal readonly string Newline;
        // Line numbers of rows dropped because a later row has the same word, each with the line
        // of the row that took its place.
        internal readonly List<(int Dropped, int Kept)> Duplicates = new List<(int, int)>();

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
            // Each nonempty line with its line number, counting LF line ends.
            var lines = new List<(string Text, int Number)>();
            var physical = text.Split('\n');
            for (var n = 0; n < physical.Length; n++)
            {
                foreach (var part in physical[n].Split(new[] { '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    lines.Add((part, n + 1));
                }
            }
            if (lines.Count == 0)
            {
                problem = "the word table is empty";
                return null;
            }
            var header = lines[0].Text.Split(',');
            var columns = WordTableColumns.From(header, out var missing);
            if (columns == null)
            {
                problem = $"the word table has no {missing} column";
                return null;
            }
            var rows = new List<string[]>();
            var shortRows = new HashSet<string[]>();
            // The game keeps rows by word, so a later row replaces an earlier one in its place.
            var byWord = new Dictionary<string, (int Index, int Line)>();
            var duplicates = new List<(int, int)>();
            for (var i = 1; i < lines.Count; i++)
            {
                var cells = StripColons(lines[i].Text).Split(',');
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
                else if (cells[columns.WordCol] == "")
                {
                    // The game skips a row with an empty word.
                    continue;
                }
                var word = cells[columns.WordCol];
                if (word != "" && byWord.TryGetValue(word, out var earlier))
                {
                    shortRows.Remove(rows[earlier.Index]);
                    rows[earlier.Index] = cells;
                    duplicates.Add((earlier.Line, lines[i].Number));
                    byWord[word] = (earlier.Index, lines[i].Number);
                    continue;
                }
                if (word != "")
                {
                    byWord[word] = (rows.Count, lines[i].Number);
                }
                rows.Add(cells);
            }
            var table = new WordTable(lines[0].Text, header, columns, rows, shortRows, text.Contains("\r\n") ? "\r\n" : "\n");
            table.Duplicates.AddRange(duplicates);
            return table;
        }

        // The game removes every ':' from a row before reading it. Its own table puts one before
        // number IDs like 1e2, so that a spreadsheet keeps them as text.
        internal static string StripColons(string line) => line.Replace(":", "");
    }
}
