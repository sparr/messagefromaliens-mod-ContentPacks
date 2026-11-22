using System;
using System.Collections.Generic;

namespace ContentPacks
{
    // Global.Init finds each word's categories with Global.Search_Radicial and draws each glyph
    // with AlienInfo.GetCharacter. Both recurse once for each level of words built on words, so a
    // table with a deep enough chain overflows the game's 1 MB main thread stack while it loads.
    // These do the same work, in the same order, with stacks on the heap.
    internal static class IterativeInit
    {
        // A depth the game's stack can't come near. Only a table whose search never ends gets
        // this deep; the game's own search overflows on it.
        private const int MaxDepth = 1 << 20;

        private enum Step { OnePart, Parts, AfterPart }

        private sealed class Frame
        {
            public string Word;
            public AlienInfo Info;
            public Step Step;
            public int Index;
            public string Item;
        }

        // Does what Global.Search_Radicial(start) does, statement for statement: the same
        // category_depth entries, added in the same order, and the same exception at the same
        // point when the game's would throw. Each frame is one of the game's calls, paused where
        // it calls itself.
        internal static void SearchRadicial(string start)
        {
            var text = Global.alien_text;
            var categories = Global.category_list;
            var limit = Math.Max(MaxDepth, 2 * text.Count + 2);
            var stack = new List<Frame>();
            var enter = start;
            while (true)
            {
                if (enter != null)
                {
                    var a = enter;
                    enter = null;
                    var info = text[a];
                    var depth = info.category_depth;
                    if (depth.Count > 0)
                    {
                    }
                    else if (info.characterstruct == PosRelation.Single || info.characterstruct == PosRelation.Solid)
                    {
                        if (info.redirect != "" && info.redirect != "token")
                        {
                            depth.Add(info.redirect, 0);
                        }
                        else
                        {
                            depth.Add(a, 0);
                        }
                    }
                    else if (info.characterstruct == PosRelation.Exponent)
                    {
                        depth.Add(a.Substring(0, 1), 0);
                    }
                    else
                    {
                        if (categories.Contains(a))
                        {
                            depth.Add(a, 0);
                        }
                        if (stack.Count >= limit)
                        {
                            throw new InvalidOperationException($"The category search from {start} went {limit} words deep without ending");
                        }
                        if (IsOnePart(info.characterstruct))
                        {
                            stack.Add(new Frame { Word = a, Info = info, Step = Step.OnePart });
                            enter = info.component[0];
                            continue;
                        }
                        stack.Add(new Frame { Word = a, Info = info, Step = Step.Parts });
                    }
                }
                if (stack.Count == 0)
                {
                    return;
                }
                var f = stack[stack.Count - 1];
                var own = f.Info.category_depth;
                switch (f.Step)
                {
                    case Step.OnePart:
                    {
                        var part = f.Info.component[0];
                        if (categories.Contains(part))
                        {
                            own.Add(part, 0);
                        }
                        else
                        {
                            var from = text[part].category_depth;
                            foreach (var key in from.Keys)
                            {
                                own.Add(key, from[key]);
                            }
                        }
                        stack.RemoveAt(stack.Count - 1);
                        continue;
                    }
                    case Step.AfterPart:
                    {
                        var item = f.Item;
                        if (categories.Contains(item))
                        {
                            if (!own.ContainsKey(item))
                            {
                                own.Add(item, 1);
                            }
                        }
                        else
                        {
                            var from = text[item].category_depth;
                            foreach (var key in from.Keys)
                            {
                                if (!own.ContainsKey(key))
                                {
                                    own.Add(key, from[key] + 1);
                                }
                                else if (from[key] + 1 < own[key])
                                {
                                    own[key] = from[key] + 1;
                                }
                            }
                        }
                        f.Step = Step.Parts;
                        break;
                    }
                }
                // Step.Parts: the game's loop over the components, resumed after the last one
                // it searched.
                var components = f.Info.component;
                while (f.Index < components.Count)
                {
                    var item = components[f.Index++];
                    if (item == "")
                    {
                        continue;
                    }
                    if (f.Word != "move" && item == "hand" && f.Info.characterstruct == PosRelation.UpDown && components[0] == "hand" && components[1] != "hand")
                    {
                        own.Add("move", 1);
                        continue;
                    }
                    f.Item = item;
                    f.Step = Step.AfterPart;
                    enter = item;
                    break;
                }
                if (enter == null)
                {
                    stack.RemoveAt(stack.Count - 1);
                }
            }
        }

        // The structures Search_Radicial reads from the first component alone.
        private static bool IsOnePart(PosRelation r) =>
            r == PosRelation.Double || r == PosRelation.VerticalDouble || r == PosRelation.Triple || r == PosRelation.Quad
            || r == PosRelation.LRFlip || r == PosRelation.UDFlip || r == PosRelation.Rotate;

        private sealed class DrawFrame
        {
            public AlienInfo Info;
            public List<AlienInfo> Parts;
            public int Next;
        }

        // Draws, deepest first, each glyph that top.GetCharacter() would draw on its way, in the
        // order it would draw them, so that the game's GetCharacter, run next, finds every part
        // already drawn and goes only one level deep. GetCharacter keeps no result for a number
        // glyph (struct exp), so one of those is left for the glyph built on it to draw, as the
        // game does, after the digit it is drawn from. If a glyph fails to draw, this stops, and
        // the game's GetCharacter reaches the failure as it would have.
        internal static void PrepareCharacter(AlienInfo top)
        {
            if (top.character != null)
            {
                return;
            }
            var stack = new List<DrawFrame> { new DrawFrame { Info = top, Parts = Parts(top) } };
            var open = new HashSet<AlienInfo> { top };
            while (stack.Count > 0)
            {
                var f = stack[stack.Count - 1];
                if (f.Next < f.Parts.Count)
                {
                    var part = f.Parts[f.Next++];
                    if (part.character == null)
                    {
                        // A glyph drawn from itself makes the game's GetCharacter call itself
                        // until the stack overflows.
                        if (!open.Add(part))
                        {
                            throw new InvalidOperationException($"The glyph of {part.word} is drawn from itself");
                        }
                        stack.Add(new DrawFrame { Info = part, Parts = Parts(part) });
                    }
                    continue;
                }
                stack.RemoveAt(stack.Count - 1);
                open.Remove(f.Info);
                if (f.Info == top || f.Info.characterstruct == PosRelation.Exponent)
                {
                    continue;
                }
                try
                {
                    f.Info.GetCharacter();
                }
                catch (Exception)
                {
                    return;
                }
                if (f.Info.character == null)
                {
                    return;
                }
            }
        }

        // The words GetCharacter draws first, through Global.StringToCharacter, in its order.
        private static List<AlienInfo> Parts(AlienInfo info)
        {
            var parts = new List<AlienInfo>(2);
            var text = Global.alien_text;
            switch (info.characterstruct)
            {
                case PosRelation.Exponent:
                    Add(parts, Reached(info.word, true));
                    break;
                case PosRelation.Single:
                case PosRelation.Solid:
                    break;
                case PosRelation.Double:
                case PosRelation.VerticalDouble:
                case PosRelation.Triple:
                case PosRelation.Quad:
                case PosRelation.Rotate:
                case PosRelation.LRFlip:
                case PosRelation.UDFlip:
                case PosRelation.Shrink:
                    Add(parts, Reached(info.component[0], false));
                    break;
                default:
                    if (text.ContainsKey(info.component[0]) && text.ContainsKey(info.component[1]))
                    {
                        parts.Add(text[info.component[0]]);
                        parts.Add(text[info.component[1]]);
                    }
                    break;
            }
            return parts;
        }

        private static void Add(List<AlienInfo> parts, string word)
        {
            if (word != null)
            {
                parts.Add(Global.alien_text[word]);
            }
        }

        // The word whose GetCharacter Global.StringToCharacter(s, init) calls, or null. It follows
        // that method's tests in its order.
        private static string Reached(string s, bool init)
        {
            while (true)
            {
                if (Global.alien_text.ContainsKey(s) && !init)
                {
                    return s;
                }
                init = false;
                if (s.StartsWith("("))
                {
                    return null;
                }
                if (s.Contains("loop-"))
                {
                    s = "loop";
                    continue;
                }
                if (s.Contains('e'))
                {
                    var halves = s.Split('e');
                    if (halves.Length == 2 && int.TryParse(halves[1], out _))
                    {
                        s = halves[0];
                        continue;
                    }
                }
                return null;
            }
        }
    }
}
