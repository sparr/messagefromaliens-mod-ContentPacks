using System;

namespace ContentPacks
{
    // Where the word table's columns are, found by the names in its header row, as the game reads
    // them. Names match regardless of case.
    internal sealed class WordTableColumns
    {
        internal const string Word = "word";
        internal const string WordType = "wordtype";
        internal const string Redirect = "redirect";
        internal const string Struct = "struct";
        internal const string Part1 = "component1";
        internal const string Part2 = "component2";
        internal const string Modifier = "modifier";

        internal int WordCol;
        internal int WordTypeCol;
        internal int RedirectCol;
        internal int StructCol;
        internal int Part1Col;
        internal int Part2Col;
        internal int ModifierCol;

        // The columns of a header, or null with the first missing name.
        internal static WordTableColumns From(string[] header, out string missing)
        {
            missing = null;
            var c = new WordTableColumns();
            foreach (var (name, set) in new (string, Action<int>)[]
            {
                (Word, i => c.WordCol = i),
                (WordType, i => c.WordTypeCol = i),
                (Redirect, i => c.RedirectCol = i),
                (Struct, i => c.StructCol = i),
                (Part1, i => c.Part1Col = i),
                (Part2, i => c.Part2Col = i),
                (Modifier, i => c.ModifierCol = i),
            })
            {
                var i = Find(header, name);
                if (i < 0)
                {
                    missing = name;
                    return null;
                }
                set(i);
            }
            return c;
        }

        internal static int Find(string[] header, string name) =>
            Array.FindIndex(header, h => string.Equals(h.Trim(), name, StringComparison.OrdinalIgnoreCase));
    }
}
