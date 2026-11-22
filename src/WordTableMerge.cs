using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        // The game finds a stroke's numbers with this regex and ignores the text around them.
        private static readonly Regex StrokeNumber = new Regex(@"[+-]?\d+(\.\d+)?");
        // It parses each one with float.Parse, which takes only ASCII digits.
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
        private readonly HashSet<Row> warnedOdd = new HashSet<Row>();
        private readonly HashSet<Row> warnedText = new HashSet<Row>();
        // The game's own rows by word, aligned to this table's columns.
        private readonly Dictionary<string, string[]> stockRows = new Dictionary<string, string[]>();
        // Each word's dictionary categories in the game's own table.
        private Dictionary<string, List<string>> stockCategories;

        private WordTableMerge(WordTable table, WordTable stock, IDictionary<string, string> textfiles)
        {
            this.table = table;
            cols = table.Columns;
            this.textfiles = textfiles;
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
            foreach (var (dropped, kept) in table.Duplicates)
            {
                Log.Warning($"{TableLabel}: line {dropped} is dropped, because line {kept} has the same word and the game uses only the last row for a word");
            }
            var stock = stockText == null ? null : WordTable.Parse(stockText, out _);
            var merge = new WordTableMerge(table, stock, textfiles);
            // The table is written back without the dropped rows.
            merge.modified = table.Duplicates.Count > 0;
            return merge.Run(text, files);
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
            if (undos > 0)
            {
                Log.Warning($"{Target}: {(undos == 1 ? "1 change was" : $"{undos} changes were")} undone; checking the rows again after them took {undoTime.TotalMilliseconds:F0} ms");
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
        // undone. The checks run in rounds over pending, in order, until a round undoes nothing.
        // A round checks a row again only when an undo since its last check could change its
        // result, since any other row would pass again.
        private bool Validate(List<Row> pending)
        {
            var undone = false;
            // Rows may have changed since the last call, as merges do.
            cycleRows = null;
            categoryLoops = null;
            gaps = null;
            BuildUsers();
            var position = new Dictionary<Row, int>();
            for (var i = 0; i < pending.Count; i++)
            {
                position[pending[i]] = i;
            }
            var round = new SortedSet<int>(Enumerable.Range(0, pending.Count));
            var next = new SortedSet<int>();
            var cursor = -1;
            // A row after the one being checked is checked later in this round, any other in the
            // next.
            void Recheck(Row r)
            {
                if (position.TryGetValue(r, out var p))
                {
                    (p > cursor ? round : next).Add(p);
                }
            }
            void RecheckAll() => pending.ForEach(Recheck);
            Stopwatch sinceUndo = null;
            while (round.Count > 0)
            {
                while (round.Count > 0)
                {
                    cursor = round.Min;
                    round.Remove(cursor);
                    var row = pending[cursor];
                    if (row.Changes.Count == 0 || IsStock(row))
                    {
                        continue;
                    }
                    var problem = row.Removed ? RemovalProblem(row) ?? GapReason(row) : RowProblem(row);
                    if (problem == null)
                    {
                        continue;
                    }
                    undone = true;
                    undos++;
                    sinceUndo = sinceUndo ?? Stopwatch.StartNew();
                    var origin = Undo(row, Recheck, RecheckAll);
                    Log.Error($"{origin}: {problem}; {(row.Removed ? (row.Stock == null ? "the new row is skipped" : "the row stays removed") : "that change is undone")}");
                }
                (round, next) = (next, round);
                cursor = -1;
            }
            if (sinceUndo != null)
            {
                undoTime += sinceUndo.Elapsed;
            }
            return undone;
        }

        // Undoes a row's latest change and updates the caches the checks use. Passes recheck each
        // row whose check could now give a different result, or calls recheckAll when that is
        // unknown. Returns the change's origin.
        private string Undo(Row row, Action<Row> recheck, Action recheckAll)
        {
            // Rows the index doesn't hold have an empty word, which no row names, and the caches
            // leave them out.
            var held = index.TryGetValue(row.Word, out var own) && own == row;
            var oldNames = held ? Names(row) : new List<string>();
            var oldParts = held && !row.Removed ? new HashSet<Row>(Parts(row)) : new HashSet<Row>();
            var wasRemoved = row.Removed;
            var wasInCycle = cycleRows != null && cycleRows.Contains(row);
            var before = row.Changes.Pop();
            row.Cells = before.Cells;
            row.Removed = before.Removed;
            row.Short = before.Short;
            recheck(row);
            if (!held)
            {
                return before.Origin;
            }
            var newNames = Names(row);
            foreach (var name in newNames)
            {
                AddUser(name, row);
            }
            // Rows that name this one check that it is live, and the rows it names, before or
            // now, may be removed rows checking that nothing uses them.
            if (users.TryGetValue(row.Word, out var namers))
            {
                foreach (var r in namers)
                {
                    recheck(r);
                }
            }
            foreach (var name in oldNames.Concat(newNames))
            {
                if (index.TryGetValue(name, out var named))
                {
                    recheck(named);
                }
            }
            UpdateCycles(row, wasInCycle, wasRemoved, oldParts, recheck);
            UpdateCategoryLoops(row, recheck);
            UpdateGaps(row, recheck, recheckAll);
            return before.Origin;
        }

        // The words a row names: its components, its redirect, a number word's digit, and the
        // move word for an up-down compound on the hand word. Every word a row's glyph, checks,
        // or categories come from is among them.
        private List<string> Names(Row r)
        {
            var names = new List<string>();
            if (r.Cells == null)
            {
                return names;
            }
            names.Add(r.Cells[cols.Part1Col]);
            names.Add(r.Cells[cols.Part2Col]);
            names.Add(r.Cells[cols.RedirectCol]);
            var code = r.Cells[cols.StructCol];
            if (KindOf(code) == Kind.Number && r.Word.Length > 0)
            {
                names.Add(r.Word.Substring(0, 1));
                names.Add(r.Word.Split('e')[0]);
            }
            if (code == "ud" && r.Cells[cols.Part1Col] == HandWord)
            {
                names.Add(MoveWord);
            }
            names.RemoveAll(n => n.Length == 0);
            return names;
        }

        // The rows the index holds that name each word, as Names gives. Built by each Validate,
        // and kept through its undos, so it may also hold rows that named a word only before an
        // undo.
        private Dictionary<string, HashSet<Row>> users;

        private void BuildUsers()
        {
            users = new Dictionary<string, HashSet<Row>>();
            foreach (var r in index.Values)
            {
                foreach (var name in Names(r))
                {
                    AddUser(name, r);
                }
            }
        }

        private void AddUser(string name, Row r)
        {
            if (!users.TryGetValue(name, out var set))
            {
                users[name] = set = new HashSet<Row>();
            }
            set.Add(r);
        }

        // Changes undone by Validate, and the time from each call's first undo to its end.
        private int undos;
        private TimeSpan undoTime;

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
                    if (HasOtherText(p1) && warnedText.Add(row))
                    {
                        Log.Warning($"{row.Origin}: a stroke holds text other than numbers and spaces, which the game ignores");
                    }
                    if (HasOddStroke(p1) && warnedOdd.Add(row))
                    {
                        Log.Warning($"{row.Origin}: a stroke has an odd count of numbers, so the game drops its last number");
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
            var gap = GapReason(row);
            if (gap != null)
            {
                return gap;
            }
            return null;
        }

        // The game draws an empty first component as a default stroke, so empty is allowed. An
        // empty stroke, as a trailing ; makes, is a path with no points, which draws nothing. The
        // game stops loading when float.Parse fails on a number its regex finds, as on non-ASCII
        // digits or a value too large for a float.
        private static string StrokeProblem(string s)
        {
            foreach (var stroke in s.Split(';'))
            {
                foreach (Match m in StrokeNumber.Matches(stroke))
                {
                    if (!Number.IsMatch(m.Value) || !float.TryParse(m.Value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out _))
                    {
                        return "a stroke holds a number the game can't read";
                    }
                }
            }
            return null;
        }

        // Text the game's regex skips, other than spaces, is likely a mistake.
        private static bool HasOtherText(string s) =>
            s.Split(';').Any(stroke => StrokeNumber.Replace(stroke, " ").Any(c => !char.IsWhiteSpace(c)));

        // The game reads a stroke's numbers in pairs and ignores a last one left over.
        private static bool HasOddStroke(string s) =>
            s.Split(';').Any(stroke => StrokeNumber.Matches(stroke).Count % 2 != 0);

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
            // The dictionary page lists every one of these categories, and fails to draw without
            // the word.
            if (Global.category_list != null && Global.category_list.Contains(word))
            {
                return "a dictionary category's word can't be removed";
            }
            var count = !users.TryGetValue(word, out var namers) ? 0 : namers.Count(r => !r.Removed && r != row
                && (r.Cells[cols.Part1Col] == word || r.Cells[cols.Part2Col] == word
                    || (r.Cells[cols.RedirectCol] == word && KindOf(r.Cells[cols.StructCol]) == Kind.Strokes)
                    || IsNumberOf(r, word)));
            return count > 0 ? $"can't remove a word that {count} other rows use as a component, redirect, or number digit" : null;
        }

        private bool Live(string word) =>
            index.TryGetValue(word, out var r) && !r.Removed;

        private bool InCycle(Row start) => CycleRows().Contains(start);

        // Rows whose glyph is built from itself: those with a part that is the row itself, and
        // those in a strongly connected component of more than one row in the graph of rows and
        // their parts. Found with one pass of Tarjan's algorithm, iterative so that a long chain
        // of words can't overflow the stack, in time linear in rows and parts. Built when first
        // needed by each Validate, and kept current through its undos by UpdateCycles.
        private HashSet<Row> cycleRows;

        // Every part edge an undo removes or adds touches the undone row. Removed edges were on
        // no cycle unless the row was on one, and a new cycle passes through the row.
        private void UpdateCycles(Row row, bool wasInCycle, bool wasRemoved, HashSet<Row> oldParts, Action<Row> recheck)
        {
            if (cycleRows == null)
            {
                return;
            }
            if (wasInCycle)
            {
                var old = cycleRows;
                cycleRows = null;
                foreach (var r in CycleRows().Union(old).Where(r => old.Contains(r) != cycleRows.Contains(r)).ToList())
                {
                    recheck(r);
                }
                return;
            }
            if (row.Removed || (!wasRemoved && Parts(row).All(oldParts.Contains)))
            {
                return;
            }
            foreach (var r in CycleThrough(row))
            {
                if (cycleRows.Add(r))
                {
                    recheck(r);
                }
            }
        }

        // The rows on cycles through start: those it reaches through parts that reach it back.
        private List<Row> CycleThrough(Row start)
        {
            var reached = new HashSet<Row> { start };
            var stack = new Stack<Row>();
            stack.Push(start);
            var self = false;
            while (stack.Count > 0)
            {
                var r = stack.Pop();
                foreach (var p in Parts(r))
                {
                    self |= r == start && p == start;
                    if (reached.Add(p))
                    {
                        stack.Push(p);
                    }
                }
            }
            // A row that reaches start through rows outside reached is itself outside it.
            var back = new HashSet<Row> { start };
            stack.Push(start);
            while (stack.Count > 0)
            {
                var r = stack.Pop();
                if (!users.TryGetValue(r.Word, out var namers))
                {
                    continue;
                }
                foreach (var u in namers)
                {
                    if (reached.Contains(u) && !back.Contains(u) && Parts(u).Contains(r))
                    {
                        back.Add(u);
                        stack.Push(u);
                    }
                }
            }
            return back.Count > 1 || self ? back.ToList() : new List<Row>();
        }

        private HashSet<Row> CycleRows()
        {
            if (cycleRows != null)
            {
                return cycleRows;
            }
            cycleRows = new HashSet<Row>();
            var order = new Dictionary<Row, int>();
            var low = new Dictionary<Row, int>();
            var open = new Stack<Row>();
            var onOpen = new HashSet<Row>();
            var work = new Stack<(Row Row, IEnumerator<Row> Parts)>();
            void Enter(Row r)
            {
                order[r] = low[r] = order.Count;
                open.Push(r);
                onOpen.Add(r);
                work.Push((r, Parts(r).GetEnumerator()));
            }
            foreach (var root in index.Values)
            {
                // A removed row is no row's part, so it is in no cycle.
                if (root.Removed || order.ContainsKey(root))
                {
                    continue;
                }
                Enter(root);
                while (work.Count > 0)
                {
                    var (r, parts) = work.Peek();
                    if (parts.MoveNext())
                    {
                        var p = parts.Current;
                        if (p == r)
                        {
                            cycleRows.Add(r);
                        }
                        if (!order.ContainsKey(p))
                        {
                            Enter(p);
                        }
                        else if (onOpen.Contains(p))
                        {
                            low[r] = Math.Min(low[r], order[p]);
                        }
                        continue;
                    }
                    work.Pop();
                    if (work.Count > 0)
                    {
                        var parent = work.Peek().Row;
                        low[parent] = Math.Min(low[parent], low[r]);
                    }
                    if (low[r] == order[r])
                    {
                        var component = new List<Row>();
                        Row member;
                        do
                        {
                            member = open.Pop();
                            onOpen.Remove(member);
                            component.Add(member);
                        }
                        while (member != r);
                        if (component.Count > 1)
                        {
                            cycleRows.UnionWith(component);
                        }
                    }
                }
            }
            return cycleRows;
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
        // case for an up-down compound on the hand word. Holds the rows to blame, as CategoryLoops
        // picks them. Built when first needed by each Validate, and again after an undo of a row
        // it looked up.
        private HashSet<Row> categoryLoops;
        // The words looked up while building categoryLoops.
        private HashSet<string> loopWords;

        private void UpdateCategoryLoops(Row row, Action<Row> recheck)
        {
            if (categoryLoops == null || Global.category_list == null
                || (!Global.category_list.Contains(row.Word) && !loopWords.Contains(row.Word)))
            {
                return;
            }
            var old = categoryLoops;
            categoryLoops = null;
            foreach (var r in CategoryLoops().Union(old).Where(r => old.Contains(r) != categoryLoops.Contains(r)).ToList())
            {
                recheck(r);
            }
        }

        private const string MoveWord = "move";
        private const string HandWord = "hand";

        private HashSet<Row> CategoryLoops()
        {
            if (categoryLoops != null)
            {
                return categoryLoops;
            }
            categoryLoops = new HashSet<Row>();
            loopWords = new HashSet<string>();
            if (Global.category_list == null)
            {
                return categoryLoops;
            }
            foreach (var category in Global.category_list.Distinct())
            {
                if (!index.TryGetValue(category, out var row) || row.Removed || KindOf(row.Cells[cols.StructCol]) != Kind.OnePart)
                {
                    continue;
                }
                loopWords.Add(row.Cells[cols.Part1Col]);
                if (!index.TryGetValue(row.Cells[cols.Part1Col], out var part) || part.Removed || Global.category_list.Contains(part.Word))
                {
                    continue;
                }
                var memo = new Dictionary<Row, bool>();
                if (!HasCategory(part, row.Word, memo))
                {
                    continue;
                }
                // The category word's own change is blamed when it alters the categories the
                // word gives, and otherwise those of the rows on the way to the source that do.
                // Failing both, as when an earlier change is to blame, the rows whose category
                // cells differ from the game's are, and failing those, the category word and,
                // when it is the game's own row, every row on the way.
                if (ChangesCategories(row))
                {
                    categoryLoops.Add(row);
                    continue;
                }
                var path = memo.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
                var culprits = path.Where(ChangesCategories).ToList();
                if (culprits.Count == 0)
                {
                    culprits = path.Where(r => !IsStock(r) && CategoryCells(r.Cells, r.Removed) != CategoryCells(r.Stock, r.Stock == null)).ToList();
                }
                if (culprits.Count > 0)
                {
                    categoryLoops.UnionWith(culprits);
                    continue;
                }
                categoryLoops.Add(row);
                if (IsStock(row))
                {
                    categoryLoops.UnionWith(path);
                }
            }
            return categoryLoops;
        }

        // Whether Search_Radicial gives a word that isn't a category word the category named
        // category, following its parts as the game does. memo records each row visited, false
        // while it is being worked out, so a cycle of components, which another check rejects,
        // ends there. A depth-first search with its own stack, so that a long chain of words
        // can't overflow the thread's stack. A frame's step is 0 before its first part, 1 after
        // its first part, and 2 after its second.
        private bool HasCategory(Row start, string category, Dictionary<Row, bool> memo)
        {
            if (memo.TryGetValue(start, out var known))
            {
                return known;
            }
            var work = new Stack<(Row Row, int Step)>();
            memo[start] = false;
            work.Push((start, 0));
            // The result of the frame that finished last.
            var last = false;
            while (work.Count > 0)
            {
                var (r, step) = work.Pop();
                var word = r.Word;
                var p1 = r.Cells[cols.Part1Col];
                var p2 = r.Cells[cols.Part2Col];
                bool? found = null;
                Row next = null;
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
                        found = step == 0 ? PartHasCategory(p1, category, memo, out next) : last;
                        step = 1;
                        break;
                    case Kind.TwoParts:
                        bool? first = null;
                        if (step == 0)
                        {
                            var moveCase = word != MoveWord && r.Cells[cols.StructCol] == "ud" && p1 == HandWord && p2 != HandWord;
                            first = moveCase ? MoveWord == category : PartHasCategory(p1, category, memo, out next);
                            step = 1;
                        }
                        else if (step == 1)
                        {
                            first = last;
                        }
                        if (step == 2)
                        {
                            found = last;
                        }
                        else if (first == true)
                        {
                            found = true;
                        }
                        else if (first == false)
                        {
                            found = PartHasCategory(p2, category, memo, out next);
                            step = 2;
                        }
                        break;
                    default:
                        found = false;
                        break;
                }
                if (next != null)
                {
                    // Continue this frame once the part's frame finishes.
                    work.Push((r, step));
                    memo[next] = false;
                    work.Push((next, 0));
                    continue;
                }
                memo[r] = found.Value;
                last = found.Value;
            }
            return last;
        }

        // A part that is a category word gives only itself; any other gives its own categories.
        // Gives the result, or null with next set to a row whose result must be worked out first.
        private bool? PartHasCategory(string part, string category, Dictionary<Row, bool> memo, out Row next)
        {
            next = null;
            if (Global.category_list.Contains(part))
            {
                return part == category;
            }
            loopWords.Add(part);
            if (!index.TryGetValue(part, out var r) || r.Removed)
            {
                return false;
            }
            if (memo.TryGetValue(r, out var known))
            {
                return known;
            }
            next = r;
            return null;
        }

        // The game's no-word marker. Global.Init gives a word with it as its redirect or first
        // word type, or a renamed word, only that category, and drops categories whose word has
        // it as its redirect.
        private const string NoWord = "token";
        private static readonly string[] TypeCategories = { "changeword", "linkword" };

        // Why a row's latest change leaves a word with no dictionary category, or a dictionary
        // category with no word, or null. Global.Init files each word under categories the way
        // CategoryFinder works it out. The dictionary page fails to draw while a word with none
        // is shown, and the dictionary page and note editor fail to open while a category in
        // Global.category_list is empty. A row whose latest change alters the categories it gives
        // is blamed for its own loss; a loss in any other row blames the rows UpdateBlamed finds.
        private string GapReason(Row row)
        {
            if (Global.category_list == null)
            {
                return null;
            }
            gaps = gaps ?? BuildGaps();
            var own = ChangesCategories(row);
            if ((own && gaps.NoCategory.Contains(row)) || gaps.BlamedForNoCategory.Contains(row))
            {
                return NoCategoryReason;
            }
            if ((own && gaps.Empty.Contains(row.Word) && !row.Removed && index.TryGetValue(row.Word, out var held) && held == row)
                || (own && gaps.LostBy.TryGetValue(row, out var lost) && lost.Overlaps(gaps.Empty))
                || gaps.BlamedForEmpty.Contains(row))
            {
                return EmptyReason;
            }
            return null;
        }

        private const string NoCategoryReason = "it leaves a word with no dictionary category, so the dictionary page fails to draw while that word is shown";
        private const string EmptyReason = "it leaves a dictionary category with no words, so the dictionary page and the note editor fail to open";

        // Every live word's categories, and what GapReason reads from them. Built when first
        // needed by each Validate, and kept current through its undos by UpdateGaps.
        private Gaps gaps;
        // The categories among the first 10 of any word's in the game's own table.
        private HashSet<string> filledBefore;

        private sealed class Gaps
        {
            public CategoryFinder Finder;
            // Built with no cycle of parts, so that each word's categories depend only on the
            // words it names, and an undo can redo just the words built on the undone row.
            public bool Exact;
            public readonly Dictionary<Row, List<string>> Now = new Dictionary<Row, List<string>>();
            // Live words with no category, where the game's own table gives them one or doesn't
            // have them, and those of them whose latest changes don't alter the categories they
            // give, which other rows are blamed for.
            public readonly HashSet<Row> NoCategory = new HashSet<Row>();
            public readonly HashSet<Row> NoCategoryPassive = new HashSet<Row>();
            // The game's own words that are removed.
            public readonly HashSet<Row> RemovedStock = new HashSet<Row>();
            // How many live words have each category among their first 10.
            public readonly Dictionary<string, int> Filled = new Dictionary<string, int>();
            // The categories each word had among its first 10 in the game's table and now lacks,
            // and the reverse.
            public readonly Dictionary<Row, HashSet<string>> LostBy = new Dictionary<Row, HashSet<string>>();
            public readonly Dictionary<string, HashSet<Row>> Lost = new Dictionary<string, HashSet<Row>>();
            // Categories in Global.category_list that have words in the game's table and none now.
            public readonly HashSet<string> Empty = new HashSet<string>();
            // Changed rows to blame for the losses.
            public HashSet<Row> BlamedForNoCategory = new HashSet<Row>();
            public HashSet<Row> BlamedForEmpty = new HashSet<Row>();
        }

        private Gaps BuildGaps()
        {
            if (stockCategories == null)
            {
                stockCategories = Categories(w => stockRows.TryGetValue(w, out var c) ? c : null, stockRows.Keys);
                // UpdateDict reads each category from the first 10 of every word's categories.
                filledBefore = new HashSet<string>(stockCategories.Values.SelectMany(c => c.Take(10)));
            }
            var g = new Gaps
            {
                Finder = new CategoryFinder(w => index.TryGetValue(w, out var r) && !r.Removed ? r.Cells : null, cols),
                Exact = CycleRows().Count == 0,
            };
            foreach (var row in index.Values.ToList())
            {
                if (!row.Removed)
                {
                    SetCategories(g, row, g.Finder.Of(row.Word), null);
                }
                else if (row.Stock != null)
                {
                    g.RemovedStock.Add(row);
                }
            }
            foreach (var category in Global.category_list)
            {
                if (IsEmpty(g, category))
                {
                    g.Empty.Add(category);
                }
            }
            UpdateBlamed(g, null);
            return g;
        }

        private bool IsEmpty(Gaps g, string category) =>
            category != NoWord && (!g.Filled.TryGetValue(category, out var n) || n == 0) && filledBefore.Contains(category);

        // Records a row's categories, or null for a removed row. Adds each category whose count
        // of words changes to changed, when given.
        private void SetCategories(Gaps g, Row row, List<string> now, HashSet<string> changed)
        {
            if (g.Now.TryGetValue(row, out var old))
            {
                foreach (var c in old.Take(10).Distinct())
                {
                    g.Filled[c]--;
                    changed?.Add(c);
                }
            }
            if (now == null)
            {
                g.Now.Remove(row);
            }
            else
            {
                g.Now[row] = now;
                foreach (var c in now.Take(10).Distinct())
                {
                    g.Filled[c] = g.Filled.TryGetValue(c, out var n) ? n + 1 : 1;
                    changed?.Add(c);
                }
            }
            var none = now != null && now.Count == 0 && (!stockCategories.TryGetValue(row.Word, out var before) || before.Count > 0);
            if (none)
            {
                g.NoCategory.Add(row);
            }
            else
            {
                g.NoCategory.Remove(row);
            }
            if (none && !ChangesCategories(row))
            {
                g.NoCategoryPassive.Add(row);
            }
            else
            {
                g.NoCategoryPassive.Remove(row);
            }
            if (now == null && row.Stock != null)
            {
                g.RemovedStock.Add(row);
            }
            else
            {
                g.RemovedStock.Remove(row);
            }
            if (g.LostBy.TryGetValue(row, out var lostBefore))
            {
                foreach (var c in lostBefore)
                {
                    g.Lost[c].Remove(row);
                }
                g.LostBy.Remove(row);
            }
            if (now != null && stockCategories.TryGetValue(row.Word, out var had))
            {
                var lost = new HashSet<string>(had.Take(10).Except(now.Take(10)));
                if (lost.Count > 0)
                {
                    g.LostBy[row] = lost;
                    foreach (var c in lost)
                    {
                        if (!g.Lost.TryGetValue(c, out var set))
                        {
                            g.Lost[c] = set = new HashSet<Row>();
                        }
                        set.Add(row);
                    }
                }
            }
        }

        // Redoes the categories of the undone row and every row built on it, through the words
        // rows name. Their categories are the only ones that can change, unless there is a cycle
        // of parts, when it all is built again.
        private void UpdateGaps(Row row, Action<Row> recheck, Action recheckAll)
        {
            if (gaps == null)
            {
                return;
            }
            if (!gaps.Exact || CycleRows().Count > 0)
            {
                gaps = null;
                recheckAll();
                return;
            }
            var affected = new List<Row> { row };
            var seen = new HashSet<Row> { row };
            for (var i = 0; i < affected.Count; i++)
            {
                var word = affected[i].Word;
                if (!users.TryGetValue(word, out var namers))
                {
                    continue;
                }
                foreach (var u in namers)
                {
                    if (!u.Removed && !seen.Contains(u) && Names(u).Contains(word))
                    {
                        seen.Add(u);
                        affected.Add(u);
                    }
                }
            }
            foreach (var r in affected)
            {
                gaps.Finder.Forget(r.Word);
            }
            var changed = new HashSet<string>();
            foreach (var r in affected)
            {
                SetCategories(gaps, r, r.Removed ? null : gaps.Finder.Of(r.Word), changed);
                recheck(r);
            }
            foreach (var c in changed)
            {
                if (!Global.category_list.Contains(c) || IsEmpty(gaps, c) == gaps.Empty.Contains(c))
                {
                    continue;
                }
                if (!gaps.Empty.Remove(c))
                {
                    gaps.Empty.Add(c);
                }
                if (index.TryGetValue(c, out var own))
                {
                    recheck(own);
                }
                if (gaps.Lost.TryGetValue(c, out var lost))
                {
                    foreach (var r in lost)
                    {
                        recheck(r);
                    }
                }
            }
            UpdateBlamed(gaps, recheck);
        }

        // Finds the changed rows to blame for the losses in rows whose own latest changes aren't
        // to blame: the game's own rows, and rows changed in a way that gives the same
        // categories. A category with no words that blames no row that way blames the removals of
        // words that had it.
        private void UpdateBlamed(Gaps g, Action<Row> recheck)
        {
            if (g.NoCategoryPassive.Count == 0 && g.Empty.Count == 0 && g.BlamedForNoCategory.Count == 0 && g.BlamedForEmpty.Count == 0)
            {
                return;
            }
            var old = g.BlamedForNoCategory.Concat(g.BlamedForEmpty).ToList();
            g.BlamedForNoCategory = new HashSet<Row>(g.NoCategoryPassive.SelectMany(Culprits));
            g.BlamedForEmpty = new HashSet<Row>();
            foreach (var c in g.Empty)
            {
                var losers = g.Lost.TryGetValue(c, out var lost) ? lost.ToList() : new List<Row>();
                var own = index.TryGetValue(c, out var r) && !r.Removed ? r : null;
                var blamed = losers.Where(l => !ChangesCategories(l)).SelectMany(Culprits).ToList();
                if (blamed.Count > 0 || losers.Any(ChangesCategories) || (own != null && ChangesCategories(own)))
                {
                    g.BlamedForEmpty.UnionWith(blamed);
                    continue;
                }
                // Otherwise the removals of words that had it.
                blamed = g.RemovedStock.Where(x => ChangesCategories(x) && stockCategories[x.Word].Take(10).Contains(c)).ToList();
                if (blamed.Count == 0)
                {
                    // Otherwise every changed row that could have emptied it, as before.
                    blamed = losers.SelectMany(Sources).Where(x => !IsStock(x)).ToList();
                    if (own != null && !IsStock(own))
                    {
                        blamed.Add(own);
                    }
                }
                g.BlamedForEmpty.UnionWith(blamed);
            }
            if (recheck != null)
            {
                foreach (var r in old.Concat(g.BlamedForNoCategory).Concat(g.BlamedForEmpty))
                {
                    recheck(r);
                }
            }
        }

        // The changed rows to blame for a loss in a row whose own latest change isn't to blame:
        // those its categories come from whose latest changes alter the categories they give. A
        // change to strokes or a meaning, say, is kept. When there are none, an earlier change
        // must be to blame, so the rows whose category cells differ from the game's are blamed,
        // and failing that, every changed row the categories come from.
        private List<Row> Culprits(Row loser)
        {
            var changed = Sources(loser).Where(r => !IsStock(r)).ToList();
            var culprits = changed.Where(ChangesCategories).ToList();
            if (culprits.Count == 0)
            {
                culprits = changed.Where(r => CategoryCells(r.Cells, r.Removed) != CategoryCells(r.Stock, r.Stock == null)).ToList();
            }
            return culprits.Count > 0 ? culprits : changed;
        }

        // Whether undoing a row's latest change would alter the categories it gives itself and
        // the rows built on it.
        private bool ChangesCategories(Row r) =>
            !IsStock(r) && r.Changes.Count > 0 && CategoryCells(r.Cells, r.Removed) != CategoryCells(r.Changes.Peek().Cells, r.Changes.Peek().Removed);

        // The cells a row's categories, and those it gives the rows built on it, are worked out
        // from, joined, or null for no row: its structure code, the components the code builds
        // it from, its redirect, its rename, and its word types.
        private string CategoryCells(string[] cells, bool removed)
        {
            if (removed || cells == null)
            {
                return null;
            }
            var code = cells[cols.StructCol];
            var kind = KindOf(code);
            return string.Join(",", code,
                kind == Kind.OnePart || kind == Kind.TwoParts ? cells[cols.Part1Col] : "",
                kind == Kind.TwoParts ? cells[cols.Part2Col] : "",
                cells[cols.RedirectCol], cells[cols.RenameCol], cells[cols.WordTypeCol]);
        }

        // The rows a row's categories come from, itself included, following its parts, a
        // number's digit, and a basic glyph's redirect, and the removed rows these name. The word
        // of each category it has is among them, and can drop that category.
        private IEnumerable<Row> Sources(Row start)
        {
            var seen = new HashSet<Row>();
            var stack = new Stack<Row>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                var r = stack.Pop();
                if (!seen.Add(r))
                {
                    continue;
                }
                var names = new List<string>();
                var kind = KindOf(r.Cells[cols.StructCol]);
                switch (kind)
                {
                    case Kind.Strokes:
                        names.Add(r.Cells[cols.RedirectCol]);
                        break;
                    case Kind.Number:
                        names.Add(r.Word.Substring(0, 1));
                        break;
                    case Kind.OnePart:
                        names.Add(r.Cells[cols.Part1Col]);
                        break;
                    case Kind.TwoParts:
                        names.Add(r.Cells[cols.Part1Col]);
                        names.Add(r.Cells[cols.Part2Col]);
                        if (r.Cells[cols.StructCol] == "ud" && r.Cells[cols.Part1Col] == HandWord)
                        {
                            names.Add(MoveWord);
                        }
                        break;
                }
                foreach (var name in names)
                {
                    if (!index.TryGetValue(name, out var next))
                    {
                        continue;
                    }
                    if (next.Removed)
                    {
                        // Its removal can drop the categories it gave.
                        seen.Add(next);
                    }
                    else
                    {
                        stack.Push(next);
                    }
                }
            }
            return seen;
        }

        // Each word's dictionary categories, by word.
        private Dictionary<string, List<string>> Categories(Func<string, string[]> cellsOf, IEnumerable<string> words)
        {
            var finder = new CategoryFinder(cellsOf, cols);
            var result = new Dictionary<string, List<string>>();
            foreach (var word in words)
            {
                result[word] = finder.Of(word);
            }
            return result;
        }

        // Works out each word's dictionary categories, as Global.Init and Search_Radicial do.
        // cellsOf gives a word's row, or null for no word. A word that names no word where the
        // game needs one, or is built from itself, fails other checks; here it just adds nothing.
        // Each word's category depths are kept until Forget drops them.
        private sealed class CategoryFinder
        {
            private readonly Func<string, string[]> cellsOf;
            private readonly WordTableColumns cols;
            private readonly Dictionary<string, Dictionary<string, int>> depths = new Dictionary<string, Dictionary<string, int>>();
            private readonly HashSet<string> visiting = new HashSet<string>();

            internal CategoryFinder(Func<string, string[]> cellsOf, WordTableColumns cols)
            {
                this.cellsOf = cellsOf;
                this.cols = cols;
            }

            internal void Forget(string word) => depths.Remove(word);

            // A word that is being worked out, and the parts it still has to merge.
            private sealed class Frame
            {
                public string Word;
                public string Code;
                public Kind Kind;
                public string[] Items;
                // The next item to look at, and whether the depths in hand are that item's.
                public int Next;
                public bool Merging;
                public Dictionary<string, int> D;
            }

            // Each category a word falls under, with its depth. A word being worked out gives
            // nothing to a word built on it, as in a cycle of components, which another check
            // rejects. A depth-first search with its own stack, so that a long chain of words
            // can't overflow the thread's stack.
            private Dictionary<string, int> Depths(string a)
            {
                if (depths.TryGetValue(a, out var known))
                {
                    return known;
                }
                var cells = cellsOf(a);
                if (cells == null || !visiting.Add(a))
                {
                    return new Dictionary<string, int>();
                }
                var work = new Stack<Frame>();
                work.Push(Begin(a, cells));
                // The depths of the word that finished last.
                Dictionary<string, int> last = null;
                while (work.Count > 0)
                {
                    var f = work.Peek();
                    if (f.Merging)
                    {
                        Merge(f, last);
                        f.Merging = false;
                        f.Next++;
                    }
                    var part = NextPart(f);
                    if (part == null)
                    {
                        visiting.Remove(f.Word);
                        depths[f.Word] = f.D;
                        last = f.D;
                        work.Pop();
                        continue;
                    }
                    f.Merging = true;
                    if (depths.TryGetValue(part, out known))
                    {
                        last = known;
                        continue;
                    }
                    var partCells = cellsOf(part);
                    if (partCells == null || !visiting.Add(part))
                    {
                        last = new Dictionary<string, int>();
                        continue;
                    }
                    work.Push(Begin(part, partCells));
                }
                return last;
            }

            // Starts working out a word, with the depths that need no part's.
            private Frame Begin(string a, string[] cells)
            {
                var f = new Frame
                {
                    Word = a,
                    Code = cells[cols.StructCol],
                    Kind = KindOf(cells[cols.StructCol]),
                    Items = new[] { cells[cols.Part1Col], cells[cols.Part2Col] },
                    D = new Dictionary<string, int>(),
                };
                switch (f.Kind)
                {
                    case Kind.Strokes:
                        var redirect = cells[cols.RedirectCol];
                        f.D[redirect != "" && redirect != NoWord ? redirect : a] = 0;
                        f.Next = f.Items.Length;
                        break;
                    case Kind.Number:
                        f.D[a.Substring(0, 1)] = 0;
                        f.Next = f.Items.Length;
                        break;
                    case Kind.OnePart:
                        if (Global.category_list.Contains(a))
                        {
                            f.D[a] = 0;
                        }
                        f.Items = new[] { f.Items[0] };
                        break;
                    case Kind.TwoParts:
                        if (Global.category_list.Contains(a))
                        {
                            f.D[a] = 0;
                        }
                        break;
                }
                return f;
            }

            // Adds the items before the next part whose depths are needed, and returns that part,
            // or null when there is none.
            private string NextPart(Frame f)
            {
                for (; f.Next < f.Items.Length; f.Next++)
                {
                    var item = f.Items[f.Next];
                    if (f.Kind == Kind.OnePart)
                    {
                        if (Global.category_list.Contains(item))
                        {
                            f.D[item] = 0;
                            continue;
                        }
                        return item;
                    }
                    if (item == "")
                    {
                        continue;
                    }
                    if (f.Word != MoveWord && item == HandWord && f.Code == "ud" && f.Items[0] == HandWord && f.Items[1] != HandWord)
                    {
                        if (!f.D.ContainsKey(MoveWord))
                        {
                            f.D[MoveWord] = 1;
                        }
                        continue;
                    }
                    if (Global.category_list.Contains(item))
                    {
                        if (!f.D.ContainsKey(item))
                        {
                            f.D[item] = 1;
                        }
                        continue;
                    }
                    return item;
                }
                return null;
            }

            // Adds a part's depths: a one-part word's as they are, a two-part word's one deeper,
            // keeping the nearest.
            private static void Merge(Frame f, Dictionary<string, int> part)
            {
                foreach (var kv in part)
                {
                    if (f.Kind == Kind.OnePart)
                    {
                        f.D[kv.Key] = kv.Value;
                    }
                    else if (!f.D.TryGetValue(kv.Key, out var have) || kv.Value + 1 < have)
                    {
                        f.D[kv.Key] = kv.Value + 1;
                    }
                }
            }
            private string RenameOf(string w, string[] cells) => cells[cols.RenameCol].Trim() == "" ? w : cells[cols.RenameCol];

            internal List<string> Of(string word)
            {
                var cells = cellsOf(word);
                var types = Global.StringToListString(cells[cols.WordTypeCol]);
                var firstType = types.Count > 0 ? types[0] : "";
                var categories = new List<string>();
                if (cells[cols.RedirectCol] == NoWord || firstType == NoWord || RenameOf(word, cells) != word)
                {
                    categories.Add(NoWord);
                    return categories;
                }
                var d = Depths(word);
                var nearest = d.Count == 0 ? 10000 : d.Values.Min();
                foreach (var kv in d)
                {
                    var source = cellsOf(kv.Key);
                    if (source != null && source[cols.RedirectCol] != NoWord && kv.Value <= nearest + 1)
                    {
                        categories.Add(RenameOf(kv.Key, source));
                    }
                }
                if (TypeCategories.Contains(firstType))
                {
                    categories.Add(firstType);
                }
                return categories;
            }
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
