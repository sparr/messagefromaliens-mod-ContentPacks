using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ContentPacks
{
    // Merges keyed rows into the word table, the comma-separated text file named Target, and
    // checks every row the packs changed.
    //
    // A merge file's first line names the columns it sets, by the names in the table's header row,
    // regardless of case. Its first column is the word column, which is the key. A later line that
    // names columns the same way starts a new set of columns. A row whose word exists overwrites
    // the named columns and keeps the rest. A row with a new word is added with the other columns
    // empty. A word written as !word removes that row. Lines starting with # and blank lines are
    // skipped.
    //
    // Rows a replaced or patched table changed are checked first, against the game's own table,
    // then the merges apply. Each change to a row is remembered: when a row fails a check, only
    // its latest change is undone, and the checks repeat, since rows that used it may now fail
    // too.
    internal sealed class WordTableMerge
    {
        internal const string Target = "newalien";

        // The merge line that last added or changed each word, for later log messages.
        internal static readonly Dictionary<string, string> Origins = new Dictionary<string, string>();

        // What a structure code's components hold.
        private enum Kind { TwoParts, OnePart, Strokes, Number }

        // The game's structure codes, as Global.Init reads them. It reads any other code as a
        // basic glyph's.
        private static readonly Dictionary<string, Kind> Kinds = new Dictionary<string, Kind>
        {
            ["s"] = Kind.Strokes, ["solid"] = Kind.Strokes,
            ["d"] = Kind.OnePart, ["t"] = Kind.OnePart, ["q"] = Kind.OnePart, ["vd"] = Kind.OnePart,
            ["flr"] = Kind.OnePart, ["fud"] = Kind.OnePart, ["fb"] = Kind.OnePart, ["shrink"] = Kind.OnePart,
            ["ud"] = Kind.TwoParts, ["lr"] = Kind.TwoParts, ["oi"] = Kind.TwoParts, ["our"] = Kind.TwoParts,
            ["oul"] = Kind.TwoParts, ["odr"] = Kind.TwoParts, ["odl"] = Kind.TwoParts, ["over"] = Kind.TwoParts,
            ["od"] = Kind.TwoParts, ["ou"] = Kind.TwoParts, ["ol"] = Kind.TwoParts, ["or"] = Kind.TwoParts,
            ["exp"] = Kind.Number,
        };

        private static Kind KindOf(string code) => Kinds.TryGetValue(code, out var kind) ? kind : Kind.Strokes;

        // The game parses stroke numbers with float.Parse, which takes only ASCII digits.
        private static readonly Regex Number = new Regex(@"^[+-]?[0-9]+(\.[0-9]+)?$");
        // A number word: a digit at a place, like 5e2.
        private static readonly Regex NumberWord = new Regex(@"^[0-9]+e-?[0-9]+$");

        private static readonly char[] AlteredChars = { ';', '<', '>' };

        private const string TableLabel = Target + " as the packs replaced or patched it";

        private sealed class State
        {
            public string[] Cells;
            public bool Removed;
            public bool Short;
            public string Origin;
        }

        private sealed class Row
        {
            public string Word;
            public string[] Cells;
            public bool Removed;
            // Shorter than the header, which stops the game while it loads.
            public bool Short;
            // The game's own row, or null for a word the game doesn't have.
            public string[] Stock;
            // The state before each change, latest last, with the change's origin.
            public readonly Stack<State> Changes = new Stack<State>();
            public string Origin => Changes.Count > 0 ? Changes.Peek().Origin : null;
        }

        private readonly WordTable table;
        private readonly WordTableColumns cols;
        private readonly IDictionary<string, string> textfiles;
        private readonly List<Row> rows = new List<Row>();
        private readonly Dictionary<string, Row> index = new Dictionary<string, Row>();
        private readonly List<Row> touched = new List<Row>();
        // Redirect values in the game's table that aren't words. The game treats them as no
        // redirect.
        private readonly HashSet<string> redirectMarkers;
        // How many times each token appears in the game's text, built when first needed.
        private Dictionary<string, int> textTokens;
        private bool modified;
        private readonly HashSet<Row> warnedCode = new HashSet<Row>();

        private WordTableMerge(WordTable table, WordTable stock, IDictionary<string, string> textfiles)
        {
            this.table = table;
            cols = table.Columns;
            this.textfiles = textfiles;
            var stockRows = new Dictionary<string, string[]>();
            foreach (var cells in stock?.Rows ?? new List<string[]>())
            {
                // The game's header and this one may order columns differently.
                var aligned = new string[table.Header.Length];
                for (var c = 0; c < aligned.Length; c++)
                {
                    var sc = Array.IndexOf(stock.Header, table.Header[c]);
                    aligned[c] = sc >= 0 && sc < cells.Length ? cells[sc] : "";
                }
                stockRows[cells[stock.Columns.WordCol]] = aligned;
            }
            foreach (var cells in table.Rows)
            {
                var word = cells[cols.WordCol];
                var row = new Row { Word = word, Cells = cells, Short = table.Short.Contains(cells) };
                stockRows.TryGetValue(word, out row.Stock);
                rows.Add(row);
                if (word != "")
                {
                    index[word] = row;
                }
                if (row.Stock == null || row.Short || !cells.SequenceEqual(row.Stock))
                {
                    row.Changes.Push(row.Stock == null
                        ? new State { Removed = true, Origin = TableLabel }
                        : new State { Cells = (string[])row.Stock.Clone(), Origin = TableLabel });
                    touched.Add(row);
                }
            }
            // The game's rows a replacement or patch left out.
            foreach (var kv in stockRows)
            {
                if (!index.ContainsKey(kv.Key))
                {
                    var row = new Row { Word = kv.Key, Cells = (string[])kv.Value.Clone(), Stock = kv.Value, Removed = true };
                    row.Changes.Push(new State { Cells = (string[])kv.Value.Clone(), Origin = TableLabel });
                    rows.Add(row);
                    index[kv.Key] = row;
                    touched.Add(row);
                }
            }
            redirectMarkers = new HashSet<string>(stockRows.Values.Select(r => r[cols.RedirectCol]).Where(v => v.Length > 0 && !stockRows.ContainsKey(v)));
        }

        // Returns the merged and checked table, or text unchanged when nothing applies. files are
        // in application order. Each item is the label used in log messages and the file's path.
        // stockText is the game's own table.
        internal static string Apply(string text, string stockText, IList<(string Label, string Path)> files, IDictionary<string, string> textfiles)
        {
            var table = WordTable.Parse(text, out var problem);
            if (table == null)
            {
                Log.Error($"{Target}: {problem}; the game's own word table is used");
                return stockText ?? text;
            }
            var stock = stockText == null ? null : WordTable.Parse(stockText, out _);
            return new WordTableMerge(table, stock, textfiles).Run(text, files);
        }

        private string Run(string text, IList<(string Label, string Path)> files)
        {
            if (touched.Count > 0)
            {
                // A replaced or patched table: its own rows are checked before any merge.
                modified |= Validate(touched.Distinct().ToList());
            }
            var fromTable = touched.Count;
            // Each row's count of changes before the merges, to tell whether any merge change
            // survives the checks.
            var depth = rows.ToDictionary(r => r, r => r.Changes.Count);
            foreach (var (label, path) in files)
            {
                try
                {
                    MergeFile(label, File.ReadAllLines(path));
                }
                catch (Exception e)
                {
                    Log.Error($"{label}: stopped by an error, keeping the rows merged before it: {e.Message}");
                }
            }
            if (touched.Count > fromTable)
            {
                var merged = touched.Skip(fromTable).Distinct().ToList();
                Validate(merged);
                // Undoing pops the latest change first, so a row back at its count is as it was.
                modified |= merged.Any(r => r.Changes.Count != (depth.TryGetValue(r, out var d) ? d : 0));
            }
            if (!modified)
            {
                if (files.Count > 0)
                {
                    Log.Info($"{Target}: no merged row is in effect, so the table is unchanged");
                }
                return text;
            }
            Origins.Clear();
            foreach (var row in touched.Distinct())
            {
                if (!row.Removed && row.Origin != null)
                {
                    Origins[row.Cells[cols.WordCol]] = row.Origin;
                }
            }
            Report();
            var output = new List<string> { table.HeaderLine };
            output.AddRange(rows.Where(r => !r.Removed).Select(r => string.Join(",", r.Cells)));
            return string.Join(table.Newline, output) + table.Newline;
        }

        private void Change(Row row, string where)
        {
            row.Changes.Push(new State { Cells = (string[])row.Cells?.Clone(), Removed = row.Removed, Short = row.Short, Origin = where });
            touched.Add(row);
        }

        private void MergeFile(string label, string[] lines)
        {
            int[] columns = null;
            var merged = 0;
            for (var n = 0; n < lines.Length; n++)
            {
                var line = lines[n].TrimEnd('\r');
                var where = $"{label}:{n + 1}";
                if (line.Trim().Length == 0 || line.StartsWith("#"))
                {
                    continue;
                }
                if (HasInnerColon(line))
                {
                    Log.Warning($"{where}: the game removes every ':' from a row, including one inside a value");
                }
                var cells = WordTable.StripColons(line).Split(',');
                // The first line names the columns, and so does a later one whose cells all do,
                // as where two merge files were joined.
                if (columns == null || (cells.Length > 1 && cells.All(c => WordTableColumns.Find(table.Header, c.Trim()) >= 0)
                    && WordTableColumns.Find(table.Header, cells[0].Trim()) == cols.WordCol))
                {
                    if (columns != null)
                    {
                        Log.Info($"{where}: a new header line; the rows after it set its columns");
                    }
                    columns = MapHeader(cells, where);
                    if (columns == null)
                    {
                        return;
                    }
                    continue;
                }
                if (cells.Length != columns.Length)
                {
                    Log.Error($"{where}: row skipped, because it has {cells.Length} cells and the header has {columns.Length}");
                    continue;
                }
                var word = cells[0];
                if (word.StartsWith("!"))
                {
                    word = word.Substring(1);
                    if (!index.TryGetValue(word, out var gone) || gone.Removed)
                    {
                        Log.Warning($"{where}: nothing to remove; no row has that word");
                        continue;
                    }
                    Change(gone, where);
                    gone.Removed = true;
                    merged++;
                    continue;
                }
                if (word.Length == 0 || word.Any(char.IsWhiteSpace))
                {
                    Log.Error($"{where}: row skipped, because its word is empty or contains a space");
                    continue;
                }
                if (!index.TryGetValue(word, out var row))
                {
                    row = new Row { Word = word, Removed = true };
                    rows.Add(row);
                    index[word] = row;
                }
                Change(row, where);
                if (row.Removed)
                {
                    // A new word, or one added again after a removal: start from empty cells.
                    row.Cells = Enumerable.Repeat("", table.Header.Length).ToArray();
                    row.Cells[cols.WordCol] = word;
                    row.Removed = false;
                    row.Short = false;
                }
                for (var c = 1; c < columns.Length; c++)
                {
                    row.Cells[columns[c]] = cells[c];
                }
                merged++;
            }
            if (columns == null)
            {
                Log.Warning($"{label}: nothing merged, because it has no header line");
            }
            else
            {
                Log.Info($"{label}: merged {Rows(merged)}");
            }
        }

        // A ':' anywhere but the start of a value, where removing it changes the value.
        private static bool HasInnerColon(string line)
        {
            foreach (var cell in line.Split(','))
            {
                foreach (var token in cell.Split(' '))
                {
                    var t = token.StartsWith("!") ? token.Substring(1) : token;
                    if (t == ":" || (t.Length > 1 && t.IndexOf(':', 1) > 0))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // The table column for each merge file column, or null when the header is bad.
        private int[] MapHeader(string[] names, string where)
        {
            var columns = new int[names.Length];
            var seen = new HashSet<int>();
            for (var i = 0; i < names.Length; i++)
            {
                var col = WordTableColumns.Find(table.Header, names[i].Trim());
                if (col < 0)
                {
                    Log.Error($"{where}: file skipped, because header column {i + 1} names no column");
                    return null;
                }
                if (!seen.Add(col))
                {
                    Log.Error($"{where}: file skipped, because header column {i + 1} repeats a column");
                    return null;
                }
                columns[i] = col;
            }
            if (columns[0] != cols.WordCol)
            {
                Log.Error($"{where}: file skipped, because the first header column must be the word column");
                return null;
            }
            return columns;
        }

        // Undoes the latest change of each row that fails, until none does. True if anything was
        // undone.
        private bool Validate(List<Row> pending)
        {
            var undone = false;
            bool changed;
            // Rows may have changed since the last call, as merges do.
            categoryLoops = null;
            do
            {
                changed = false;
                foreach (var row in pending)
                {
                    if (row.Changes.Count == 0 || IsStock(row))
                    {
                        continue;
                    }
                    var problem = row.Removed ? RemovalProblem(row) : RowProblem(row);
                    if (problem == null)
                    {
                        continue;
                    }
                    changed = true;
                    undone = true;
                    categoryLoops = null;
                    var before = row.Changes.Pop();
                    row.Cells = before.Cells;
                    row.Removed = before.Removed;
                    row.Short = before.Short;
                    Log.Error($"{before.Origin}: {problem}; {(row.Removed ? (row.Stock == null ? "the new row is skipped" : "the row stays removed") : "that change is undone")}");
                }
            }
            while (changed);
            return undone;
        }

        // The game's own row, unchanged.
        private static bool IsStock(Row r) =>
            r.Stock != null && !r.Removed && !r.Short && r.Cells.SequenceEqual(r.Stock);

        private void Report()
        {
            var all = touched.Distinct().ToList();
            foreach (var row in all.Where(r => r.Removed && r.Stock != null))
            {
                var uses = TextUses(row.Stock[cols.WordCol]);
                if (uses > 0)
                {
                    Log.Warning($"{row.Origin}: the removed word is still used {uses} times in the game's text");
                }
            }
            var changedRows = all.Count(r => r.Stock != null && !r.Removed && !IsStock(r));
            var added = all.Count(r => r.Stock == null && !r.Removed);
            var removed = all.Count(r => r.Stock != null && r.Removed);
            Log.Info($"{Target}: {Rows(added)} added, {changedRows} changed, {removed} removed");
        }

        private static string Rows(int n) => n == 1 ? "1 row" : $"{n} rows";

        private string RowProblem(Row row)
        {
            if (row.Short)
            {
                return "it has fewer cells than the header, which stops the game while it loads";
            }
            var word = row.Cells[cols.WordCol];
            // Page text can't name such a word: Draw.Write drops an e0 from the end of each token,
            // and the game's tokenizer removes < and >, and splits sentences at ;.
            if (row.Stock == null && (word.EndsWith("e0") || word.IndexOfAny(AlteredChars) >= 0))
            {
                return "the game changes a word that ends in e0 or contains ;, <, or > wherever its text names it";
            }
            // The game reads every word's meaning and word types with its tokenizer, and its first
            // word type while it starts, and stops loading when either fails.
            var problem = GameParse.Problem(row.Cells[cols.MeaningCol]) ?? GameParse.Problem(row.Cells[cols.WordTypeCol]);
            if (problem != null)
            {
                return problem.Replace("its token", "a token");
            }
            if (Global.StringToListString(row.Cells[cols.WordTypeCol]).Count == 0)
            {
                return "it has no word type";
            }
            var code = row.Cells[cols.StructCol];
            if (!Kinds.ContainsKey(code) && warnedCode.Add(row))
            {
                Log.Warning(code.Trim().Length == 0
                    ? $"{row.Origin}: it has no structure code, so the game draws the word as a basic glyph"
                    : $"{row.Origin}: the game doesn't know this structure code, so it draws the word as a basic glyph");
            }
            var p1 = row.Cells[cols.Part1Col];
            var p2 = row.Cells[cols.Part2Col];
            var kind = KindOf(code);
            switch (kind)
            {
                case Kind.TwoParts:
                    if (!Live(p1) || !Live(p2))
                    {
                        return "a component names no word";
                    }
                    break;
                case Kind.OnePart:
                    if (!Live(p1))
                    {
                        return "the first component names no word";
                    }
                    break;
                case Kind.Strokes:
                    var strokes = StrokeProblem(p1) ?? RedirectProblem(row);
                    if (strokes != null)
                    {
                        return strokes;
                    }
                    break;
                case Kind.Number:
                    // The game draws a number word from its own name, and files it under the
                    // word named by its first character.
                    if (!NumberWord.IsMatch(word))
                    {
                        return "a number word must be a digit, e, and a place, like 5e2";
                    }
                    if (!Live(word.Substring(0, 1)))
                    {
                        return "its digit names no word";
                    }
                    break;
            }
            if (kind != Kind.Strokes && InCycle(row))
            {
                return "its glyph is built from itself, through its components or a number's digit, which stops the game while it loads";
            }
            if (CategoryLoops().Contains(row))
            {
                return "it files a one-part dictionary category's word under that same category twice, which stops the game while it loads";
            }
            // The dictionary page files every word under the game's categories by these words'
            // names; it fails to draw if one is renamed or marked as no word.
            if (Global.category_list != null && Global.category_list.Contains(word))
            {
                var rename = row.Cells[cols.RenameCol].Trim();
                if (rename != "" && rename != word)
                {
                    return "a dictionary category's word can't be renamed";
                }
                var redirect = row.Cells[cols.RedirectCol];
                if (redirectMarkers.Contains(redirect))
                {
                    return "a dictionary category's word can't be marked as no word";
                }
            }
            return null;
        }

        // The game draws an empty first component as a default stroke, so empty is allowed. An
        // empty stroke, as a trailing ; makes, is a path with no points, which draws nothing.
        private static string StrokeProblem(string s)
        {
            foreach (var stroke in s.Split(';'))
            {
                var nums = stroke.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (!nums.All(n => Number.IsMatch(n) && float.TryParse(n, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out _)))
                {
                    return "a stroke holds something other than numbers";
                }
                if (nums.Length % 2 != 0)
                {
                    return "a stroke has an odd count of numbers";
                }
            }
            return null;
        }

        // A basic glyph's redirect files it under another word while the game starts, so it must
        // name a word, or be one of the game's own non-word redirect values.
        private string RedirectProblem(Row row)
        {
            var redirect = row.Cells[cols.RedirectCol];
            if (redirect.Length == 0 || redirectMarkers.Contains(redirect) || Live(redirect))
            {
                return null;
            }
            return "its redirect names no word";
        }

        private bool IsNumberOf(Row r, string digit) =>
            digit.Length == 1 && KindOf(r.Cells[cols.StructCol]) == Kind.Number && r.Cells[cols.WordCol].StartsWith(digit);

        private string RemovalProblem(Row row)
        {
            var word = row.Word;
            var users = index.Values.Count(r => !r.Removed && r != row
                && (r.Cells[cols.Part1Col] == word || r.Cells[cols.Part2Col] == word
                    || (r.Cells[cols.RedirectCol] == word && KindOf(r.Cells[cols.StructCol]) == Kind.Strokes)
                    || IsNumberOf(r, word)));
            return users > 0 ? $"can't remove a word that {users} other rows use as a component, redirect, or number digit" : null;
        }

        private bool Live(string word) =>
            index.TryGetValue(word, out var r) && !r.Removed;

        private bool InCycle(Row start)
        {
            var seen = new HashSet<Row>();
            var stack = new Stack<Row>();
            foreach (var p in Parts(start))
            {
                stack.Push(p);
            }
            while (stack.Count > 0)
            {
                var r = stack.Pop();
                if (r == start)
                {
                    return true;
                }
                if (!seen.Add(r))
                {
                    continue;
                }
                foreach (var p in Parts(r))
                {
                    stack.Push(p);
                }
            }
            return false;
        }

        // The words a word's glyph is drawn from: its components, or a number word's digits, which
        // StringToCharacter draws from the word named by the part before the e.
        private IEnumerable<Row> Parts(Row r)
        {
            var kind = KindOf(r.Cells[cols.StructCol]);
            if ((kind == Kind.TwoParts || kind == Kind.OnePart) && index.TryGetValue(r.Cells[cols.Part1Col], out var a) && !a.Removed)
            {
                yield return a;
            }
            if (kind == Kind.TwoParts && index.TryGetValue(r.Cells[cols.Part2Col], out var b) && !b.Removed)
            {
                yield return b;
            }
            if (kind == Kind.Number)
            {
                var halves = r.Cells[cols.WordCol].Split('e');
                if (halves.Length == 2 && int.TryParse(halves[1], out _) && index.TryGetValue(halves[0], out var digit) && !digit.Removed)
                {
                    yield return digit;
                }
            }
        }

        // Rows that make Global.Search_Radicial add a key twice, which throws while the game
        // loads. It files a dictionary category's word under itself first, and a one-part word
        // then gets every category its part has. So a one-part category word whose part, not a
        // category word itself, has that word among its categories stops the game. The part gets
        // it from a basic glyph whose redirect names the word, or from the game's one special
        // case for an up-down compound on the hand word. Holds the category word, and when it is
        // the game's own row, every row on the way to the source. Built when first needed after
        // any row changes.
        private HashSet<Row> categoryLoops;

        private const string MoveWord = "move";
        private const string HandWord = "hand";

        private HashSet<Row> CategoryLoops()
        {
            if (categoryLoops != null)
            {
                return categoryLoops;
            }
            categoryLoops = new HashSet<Row>();
            if (Global.category_list == null)
            {
                return categoryLoops;
            }
            foreach (var row in index.Values)
            {
                if (row.Removed || KindOf(row.Cells[cols.StructCol]) != Kind.OnePart || !Global.category_list.Contains(row.Word)
                    || !index.TryGetValue(row.Cells[cols.Part1Col], out var part) || part.Removed || Global.category_list.Contains(part.Word))
                {
                    continue;
                }
                var memo = new Dictionary<Row, bool>();
                if (HasCategory(part, row.Word, memo))
                {
                    // The category word's own change is blamed first, when it has one.
                    categoryLoops.Add(row);
                    if (IsStock(row))
                    {
                        categoryLoops.UnionWith(memo.Where(kv => kv.Value).Select(kv => kv.Key));
                    }
                }
            }
            return categoryLoops;
        }

        // Whether Search_Radicial gives a word that isn't a category word the category named
        // category, following its parts as the game does. memo records each row visited.
        private bool HasCategory(Row r, string category, Dictionary<Row, bool> memo)
        {
            if (memo.TryGetValue(r, out var known))
            {
                return known;
            }
            // A cycle of components, which another check rejects.
            memo[r] = false;
            var found = false;
            var word = r.Word;
            var p1 = r.Cells[cols.Part1Col];
            var p2 = r.Cells[cols.Part2Col];
            switch (KindOf(r.Cells[cols.StructCol]))
            {
                case Kind.Strokes:
                    var redirect = r.Cells[cols.RedirectCol];
                    found = (redirect.Length > 0 && !redirectMarkers.Contains(redirect) ? redirect : word) == category;
                    break;
                case Kind.Number:
                    found = word.Substring(0, 1) == category;
                    break;
                case Kind.OnePart:
                    found = PartHasCategory(p1, category, memo);
                    break;
                case Kind.TwoParts:
                    var moveCase = word != MoveWord && r.Cells[cols.StructCol] == "ud" && p1 == HandWord && p2 != HandWord;
                    found = (moveCase ? MoveWord == category : PartHasCategory(p1, category, memo))
                        || PartHasCategory(p2, category, memo);
                    break;
            }
            memo[r] = found;
            return found;
        }

        // A part that is a category word gives only itself; any other gives its own categories.
        private bool PartHasCategory(string part, string category, Dictionary<Row, bool> memo)
        {
            if (Global.category_list.Contains(part))
            {
                return part == category;
            }
            return index.TryGetValue(part, out var r) && !r.Removed && HasCategory(r, category, memo);
        }

        // How many times a word appears in the game's text, other than the word table. The text is
        // counted once, the first time this is needed. A line's #, @, or % prefix is not part of
        // its first word.
        private int TextUses(string word)
        {
            if (textTokens == null)
            {
                textTokens = new Dictionary<string, int>();
                foreach (var kv in textfiles)
                {
                    if (kv.Key == Target)
                    {
                        continue;
                    }
                    foreach (var token in kv.Value.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var t = token.TrimStart('#', '@', '%').TrimEnd('.');
                        textTokens[t] = textTokens.TryGetValue(t, out var n) ? n + 1 : 1;
                    }
                }
            }
            return textTokens.TryGetValue(word, out var uses) ? uses : 0;
        }
    }
}
