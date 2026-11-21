using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ContentPacks
{
    // Applies a unified diff to a text file.
    //
    // Hunks apply in order, each to the result of the ones before it. If any hunk fails, the
    // whole patch is skipped.
    //
    // With enforceContext, a hunk is placed where its old text (context and removed lines)
    // matches. Without it, a hunk that removes lines is placed by its removed lines alone: its
    // context lines match any line, and the file's own lines are kept there. A hunk that only
    // inserts lines is placed where all its context matches, or failing that, where the most of
    // its context lines match, then nearest the line its header names.
    //
    // When a hunk matches several places, without enforceContext a place where its context matches
    // too wins first, then the place where the most of its context lines match, since the game's
    // files repeat lines across pages and a nearer place can be another page. Then the one nearest
    // the line its header names wins. That line is shifted by the earlier hunks' size changes and
    // by how far the last hunk landed from its own header line, as GNU patch does. Any remaining
    // tie is an error. A hunk with no context or removed lines, as diff -U0 writes an insertion,
    // is placed by its header's line alone.
    //
    // A hunk whose old side matches the game's own file at the line its header names is placed
    // within the page it changes there, by the rules above, when it can be, and otherwise
    // anywhere, with a warning. Any other hunk, including every hunk for a file a pack added or
    // replaced, is placed only within its page when its context includes the page's title line.
    internal static class TextPatcher
    {
        private static readonly Regex HunkHeader = new Regex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@");

        private sealed class Hunk
        {
            public int Number;
            public int PatchLine;
            // The header's old start line, 1-based, and old and new line counts.
            public int OldStart;
            public int OldCount;
            public int NewCount;
            public readonly List<char> Ops = new List<char>();
            public readonly List<string> Texts = new List<string>();
            // "\ No newline at end of file" followed the old or new side's last line.
            public bool OldNoNewline;
            public bool NewNoNewline;
        }

        // Returns the patched text, or null when the patch is skipped. where names the patch in
        // log messages. stock is the game's own version of the file, or null for a file a pack
        // added or replaced.
        internal static string Apply(string text, string patch, string where, bool enforceContext, string stock = null)
        {
            if (!Parse(patch, where, out var hunks))
            {
                return null;
            }
            var newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = SplitLines(text, out var finalNewline);
            var stockLines = stock == null ? null : SplitLines(stock, out _);
            // Lines added minus lines removed by earlier hunks, and how far the last hunk landed
            // from where its header said.
            var growth = 0;
            var drift = 0;
            foreach (var h in hunks)
            {
                var expected = h.OldStart - 1 + growth + drift;
                if (ApplyHunk(lines, h, enforceContext, where, expected, stockLines, ref finalNewline, out var at))
                {
                    drift = at - (h.OldStart - 1 + growth);
                    growth += h.NewCount - h.OldCount;
                }
                else
                {
                    Log.Error($"{where}: skipped, because hunk {h.Number} did not apply");
                    return null;
                }
            }
            var result = string.Join(newline, lines);
            return finalNewline && lines.Count > 0 ? result + newline : result;
        }

        internal static List<string> SplitLines(string text, out bool finalNewline)
        {
            var parts = new List<string>(text.Split('\n'));
            finalNewline = parts.Count > 1 && parts[parts.Count - 1].Length == 0;
            if (finalNewline || (parts.Count == 1 && parts[0].Length == 0))
            {
                parts.RemoveAt(parts.Count - 1);
            }
            for (var i = 0; i < parts.Count; i++)
            {
                if (parts[i].EndsWith("\r"))
                {
                    parts[i] = parts[i].Substring(0, parts[i].Length - 1);
                }
            }
            return parts;
        }

        private static bool Parse(string patch, string where, out List<Hunk> hunks)
        {
            hunks = new List<Hunk>();
            var lines = SplitLines(patch, out _);
            var fileHeaders = 0;
            var i = 0;
            while (i < lines.Count)
            {
                var line = lines[i];
                var m = HunkHeader.Match(line);
                if (!m.Success)
                {
                    if (line.StartsWith("+++ "))
                    {
                        fileHeaders++;
                        if (fileHeaders > 1)
                        {
                            Log.Error($"{where}:{i + 1}: skipped, because a patch file may change only one file");
                            return false;
                        }
                    }
                    else if (hunks.Count > 0 && line.Length > 0 && !line.StartsWith("--- ")
                        && !line.StartsWith("diff ") && !line.StartsWith("index "))
                    {
                        Log.Error($"{where}:{i + 1}: skipped, because this line is outside any hunk");
                        return false;
                    }
                    i++;
                    continue;
                }
                if (!int.TryParse(m.Groups[1].Value, out var oldStart)
                    || !TryCount(m.Groups[2], out var oldLeft) || !TryCount(m.Groups[4], out var newLeft))
                {
                    Log.Error($"{where}:{i + 1}: skipped, because a number in this hunk header is too large");
                    return false;
                }
                var hunk = new Hunk
                {
                    Number = hunks.Count + 1,
                    PatchLine = i + 1,
                    // A hunk that removes nothing names the line before its insertion.
                    OldStart = oldStart + (oldLeft == 0 ? 1 : 0),
                    OldCount = oldLeft,
                    NewCount = newLeft,
                };
                i++;
                while (oldLeft > 0 || newLeft > 0)
                {
                    if (i >= lines.Count)
                    {
                        Log.Error($"{where}:{hunk.PatchLine}: skipped, because hunk {hunk.Number} ends early");
                        return false;
                    }
                    var l = lines[i];
                    // Some editors strip the space that starts an empty context line.
                    var op = l.Length == 0 ? ' ' : l[0];
                    var body = l.Length == 0 ? "" : l.Substring(1);
                    if (op == '\\')
                    {
                        MarkNoNewline(hunk);
                        i++;
                        continue;
                    }
                    if ((op == ' ' && oldLeft > 0 && newLeft > 0) || (op == '-' && oldLeft > 0) || (op == '+' && newLeft > 0))
                    {
                        hunk.Ops.Add(op);
                        hunk.Texts.Add(body);
                        if (op != '+') oldLeft--;
                        if (op != '-') newLeft--;
                        i++;
                        continue;
                    }
                    Log.Error($"{where}:{i + 1}: skipped, because this line doesn't fit hunk {hunk.Number}'s line counts");
                    return false;
                }
                while (i < lines.Count && lines[i].StartsWith("\\"))
                {
                    MarkNoNewline(hunk);
                    i++;
                }
                hunks.Add(hunk);
            }
            if (hunks.Count == 0)
            {
                Log.Error($"{where}: skipped, because it has no hunks");
                return false;
            }
            return true;
        }

        // A hunk header's line count, which defaults to 1 when left out.
        private static bool TryCount(Group g, out int count)
        {
            count = 1;
            return !g.Success || int.TryParse(g.Value, out count);
        }

        private static void MarkNoNewline(Hunk h)
        {
            if (h.Ops.Count == 0)
            {
                return;
            }
            var last = h.Ops[h.Ops.Count - 1];
            if (last != '+') h.OldNoNewline = true;
            if (last != '-') h.NewNoNewline = true;
        }

        private enum Placement { Placed, None, Ambiguous }

        private static bool ApplyHunk(List<string> lines, Hunk h, bool enforceContext, string where, int expected,
            List<string> stock, ref bool finalNewline, out int at)
        {
            at = -1;
            // The hunk's old side, with which entries are context.
            var old = new List<string>();
            var oldIsContext = new List<bool>();
            for (var k = 0; k < h.Ops.Count; k++)
            {
                if (h.Ops[k] != '+')
                {
                    old.Add(h.Texts[k]);
                    oldIsContext.Add(h.Ops[k] == ' ');
                }
            }
            if (old.Count == 0)
            {
                if (expected < 0 || expected > lines.Count)
                {
                    Log.Error($"{where}:{h.PatchLine}: hunk {h.Number} has no context or removed lines, and its line number is past the end of the file");
                    return false;
                }
                at = expected;
                lines.InsertRange(at, h.Texts);
                Log.Info($"{where}:{h.PatchLine}: hunk {h.Number} has no context or removed lines, so it was placed by its line number");
                return true;
            }
            var exact = FindAll(lines, old, null);
            var loose = !enforceContext && h.Ops.Contains('-');
            // The context lines before the hunk's first added or removed line.
            var before = h.Ops.TakeWhile(op => op == ' ').Count();
            // The page of a place is the page of its last context line before the change. The
            // game's files repeat lines across pages, so the same lines elsewhere are another
            // page's.
            var titles = Titles(lines);
            string PageAt(int m) => m + before - 1 >= 0 ? titles[Math.Min(m + before - 1, lines.Count - 1)] : null;
            string Name(string title) => title == null ? "above the first page" : title.Substring(1);

            Placement placed;
            string note;
            // A hunk written against the game's own file edits the page it edits there.
            var page = StockPage(stock, h.OldStart - 1, old, before);
            if (page != null && !lines.Contains(page))
            {
                Log.Warning($"{where}:{h.PatchLine}: hunk {h.Number}'s page {Name(page)} is no longer in the file, so it was placed anywhere in the file");
                page = null;
            }
            if (page != null)
            {
                placed = Place(lines, h, old, oldIsContext, exact, loose, enforceContext, expected, before, m => PageAt(m) == page, out at, out note);
                if (placed == Placement.None)
                {
                    placed = Place(lines, h, old, oldIsContext, exact, loose, enforceContext, expected, before, null, out at, out note);
                    if (placed == Placement.Placed)
                    {
                        Log.Warning($"{where}:{h.PatchLine}: hunk {h.Number} matches nowhere on its page {Name(page)}, so it was placed on page {Name(PageAt(at))}");
                    }
                }
            }
            else
            {
                // A hunk whose context holds its page's title line edits only that page.
                var title = PageTitle(old, before);
                placed = Place(lines, h, old, oldIsContext, exact, loose, enforceContext, expected, before,
                    title == null ? (Func<int, bool>)null : m => PageAt(m) == title, out at, out note);
                if (placed == Placement.None && title != null
                    && Place(lines, h, old, oldIsContext, exact, loose, enforceContext, expected, before, null, out _, out _) != Placement.None)
                {
                    Log.Error($"{where}:{h.PatchLine}: hunk {h.Number} matches only outside its page {Name(title)}, whose lines another pack may have changed");
                    return false;
                }
            }
            if (placed == Placement.None)
            {
                Log.Error($"{where}:{h.PatchLine}: hunk {h.Number} matches nowhere");
                return false;
            }
            if (placed == Placement.Ambiguous)
            {
                Log.Error($"{where}:{h.PatchLine}: {note}");
                return false;
            }
            if (note != null)
            {
                Log.Info($"{where}:{h.PatchLine}: {note}");
            }

            // Rebuild the matched span. Context keeps the file's own line, which differs from the
            // patch's only when context isn't enforced. In that case edge context may also run
            // past the file's ends, so only the lines that exist are kept.
            var replacement = new List<string>();
            var pos = at;
            var start = Math.Max(at, 0);
            for (var k = 0; k < h.Ops.Count; k++)
            {
                switch (h.Ops[k])
                {
                    case ' ':
                        if (pos >= 0 && pos < lines.Count)
                        {
                            replacement.Add(lines[pos]);
                        }
                        pos++;
                        break;
                    case '-':
                        pos++;
                        break;
                    default:
                        replacement.Add(h.Texts[k]);
                        break;
                }
            }
            var end = Math.Min(pos, lines.Count);
            lines.RemoveRange(start, end - start);
            lines.InsertRange(start, replacement);
            // A hunk that reaches the end of the file decides whether it ends in a newline.
            if ((h.OldNoNewline || h.NewNoNewline) && start + replacement.Count == lines.Count)
            {
                finalNewline = !h.NewNoNewline;
            }
            if (loose && !exact.Contains(at))
            {
                Log.Info($"{where}:{h.PatchLine}: hunk {h.Number} applied where its context differs");
            }
            return true;
        }

        // Chooses where a hunk goes among the places allowed admits, or all places when it's null.
        // note says how a place was chosen among several, or why none could be.
        private static Placement Place(List<string> lines, Hunk h, List<string> old, List<bool> oldIsContext,
            List<int> exact, bool loose, bool enforceContext, int expected, int before, Func<int, bool> allowed,
            out int at, out string note)
        {
            at = -1;
            note = null;
            if (allowed != null)
            {
                exact = exact.Where(allowed).ToList();
            }
            List<int> matches;
            // Without enforceContext, an insertion whose context matches nowhere in full goes
            // where the most of its context lines match.
            var fuzzy = !enforceContext && !loose && exact.Count == 0;
            if (fuzzy)
            {
                // Every place the insertion can go, from the file's start to its end.
                matches = Enumerable.Range(-before, lines.Count + 1).Where(m => ContextScore(lines, old, oldIsContext, m) > 0).ToList();
            }
            else if (!loose)
            {
                matches = exact;
            }
            else
            {
                // Edge context is dropped, and context between removed lines matches anything.
                var first = oldIsContext.IndexOf(false);
                var last = oldIsContext.LastIndexOf(false);
                var core = old.GetRange(first, last - first + 1);
                var wild = oldIsContext.GetRange(first, last - first + 1);
                matches = FindAll(lines, core, wild).Select(m => m - first).ToList();
            }
            if (allowed != null)
            {
                matches = matches.Where(allowed).ToList();
            }
            if (matches.Count == 0)
            {
                return Placement.None;
            }
            if (fuzzy)
            {
                var scores = matches.ToDictionary(m => m, m => ContextScore(lines, old, oldIsContext, m));
                var best = scores.Values.Max();
                matches = matches.Where(m => scores[m] == best).ToList();
                var nearest = matches.Min(m => Math.Abs(m - expected));
                matches = matches.Where(m => Math.Abs(m - expected) == nearest).ToList();
                if (matches.Count > 1)
                {
                    note = $"hunk {h.Number}'s context matches nowhere in full, and {matches.Count} places where {best} of its {old.Count} context lines match are equally near line {expected + 1}; add context lines that tell them apart";
                    return Placement.Ambiguous;
                }
                note = $"hunk {h.Number}'s context matches nowhere in full; placed at line {matches[0] + before + 1}, where {best} of its {old.Count} context lines match";
            }
            else if (matches.Count > 1)
            {
                var places = matches.Count;
                // A place where the whole hunk matches, context included, wins over the rest.
                if (loose)
                {
                    var perfect = matches.Where(exact.Contains).ToList();
                    if (perfect.Count > 0)
                    {
                        matches = perfect;
                    }
                    var scores = matches.ToDictionary(m => m, m => ContextScore(lines, old, oldIsContext, m));
                    var best = scores.Values.Max();
                    matches = matches.Where(m => scores[m] == best).ToList();
                }
                var nearest = matches.Min(m => Math.Abs(m - expected));
                matches = matches.Where(m => Math.Abs(m - expected) == nearest).ToList();
                if (matches.Count > 1)
                {
                    note = $"hunk {h.Number} matches {places} places{(allowed != null ? " on its page" : "")}, and {matches.Count} of them are equally near line {expected + 1}{(loose ? " with as many matching context lines" : "")}; add context lines that tell them apart";
                    return Placement.Ambiguous;
                }
                note = $"hunk {h.Number} matches {places} places{(allowed != null ? " on its page" : "")}; used the one at line {Math.Max(matches[0], 0) + 1}, nearest line {expected + 1}";
            }
            at = matches[0];
            return Placement.Placed;
        }

        // The title of the page a hunk changes in the game's own file, when the hunk's old side
        // matches that file at the line its header names, or null.
        private static string StockPage(List<string> stock, int start, List<string> old, int before)
        {
            if (stock == null || start < 0 || start + old.Count > stock.Count)
            {
                return null;
            }
            for (var k = 0; k < old.Count; k++)
            {
                if (stock[start + k] != old[k])
                {
                    return null;
                }
            }
            return start + before - 1 >= 0 ? Titles(stock)[start + before - 1] : null;
        }

        // For each line, the nearest line at or above it that starts with #, or null.
        private static string[] Titles(List<string> lines)
        {
            var titles = new string[lines.Count];
            string title = null;
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].StartsWith("#"))
                {
                    title = lines[i];
                }
                titles[i] = title;
            }
            return titles;
        }

        // The last of the first count context lines that starts with #: the title of the page the
        // hunk's change is in, or null.
        private static string PageTitle(List<string> old, int count)
        {
            for (var k = count - 1; k >= 0; k--)
            {
                if (old[k].StartsWith("#"))
                {
                    return old[k];
                }
            }
            return null;
        }

        // How many of the old side's context lines match the file when the old side starts at at.
        private static int ContextScore(List<string> lines, List<string> old, List<bool> isContext, int at)
        {
            var score = 0;
            for (var k = 0; k < old.Count; k++)
            {
                var pos = at + k;
                if (isContext[k] && pos >= 0 && pos < lines.Count && lines[pos] == old[k])
                {
                    score++;
                }
            }
            return score;
        }

        // Where needle matches lines. An entry whose wild flag is set matches any line.
        private static List<int> FindAll(List<string> lines, List<string> needle, List<bool> wild)
        {
            var found = new List<int>();
            for (var s = 0; s + needle.Count <= lines.Count; s++)
            {
                var ok = true;
                for (var k = 0; k < needle.Count && ok; k++)
                {
                    ok = (wild != null && wild[k]) || lines[s + k] == needle[k];
                }
                if (ok)
                {
                    found.Add(s);
                }
            }
            return found;
        }
    }
}
