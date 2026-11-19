using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ContentPacks
{
    // Checks text the way Global.Init will read it, so a pack's mistake is reported and skipped
    // instead of stopping the game while it loads. Global.StringToListString and ParseString use
    // no game state, so they can run before the game parses its text.
    internal static class GameParse
    {
        // A number token with an exponent this large makes the game's parser loop forever, as
        // 5E400 or 1D400 do.
        private const int MaxExponent = 300;
        private static readonly Regex Exponent = new Regex(@"^-?\d+(?:\.\d+)?(?:e|E|x|A|F|DS|D)(-?\d+)");

        internal enum Read { Tokens, TitleTokens, Answer }

        // Why the game can't read the text as it reads the given kind of line, or null.
        internal static string Problem(string text, Read read = Read.Tokens)
        {
            foreach (var token in text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var m = Exponent.Match(token);
                if (m.Success && (!int.TryParse(m.Groups[1].Value, out var k) || Math.Abs(k) > MaxExponent))
                {
                    return $"its token {Shape(token)} has an exponent larger than {MaxExponent}, which makes the game loop forever";
                }
            }
            try
            {
                switch (read)
                {
                    case Read.Answer:
                        Global.ParseString(text, modify: false);
                        break;
                    case Read.TitleTokens:
                        Global.StringToListString(text, modify: false);
                        break;
                    default:
                        Global.StringToListString(text);
                        break;
                }
                return null;
            }
            catch (Exception e)
            {
                return $"the game's parser fails on it ({e.GetType().Name})";
            }
        }

        // A token's form without its letters, which may be word IDs: digits stay, so the form of
        // a number token shows.
        private static string Shape(string token) => token.Length > 20 ? token.Substring(0, 20) + "..." : token;

        // Why a page file can't be read, with its 1-based line number, or null. Lines are read as
        // Global.Init reads them: # starts a page's title, @ a quiz's supply, % an answer.
        internal static string FileProblem(string text, out int line)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                line = i + 1;
                var l = lines[i];
                var problem = l.StartsWith("#") ? Problem(l.Substring(1), Read.TitleTokens)
                    : l.StartsWith("@") ? null
                    : l.StartsWith("%") ? Problem(l.Substring(1), Read.Answer)
                    : Problem(l);
                if (problem != null)
                {
                    return problem;
                }
            }
            line = 0;
            return null;
        }

        // The text without blank lines that end a page, before the next title line or at the end
        // of the file, a final line break included. The game appends each line to its page, so
        // such a line becomes an empty sentence, and in a menu an empty link that can never be
        // won, which keeps the menu from being won.
        internal static string Tidy(string text)
        {
            var newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
            var endsPage = true;
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                if (lines[i].Trim().Length == 0 && endsPage)
                {
                    lines.RemoveAt(i);
                }
                else
                {
                    endsPage = lines[i].StartsWith("#");
                }
            }
            return string.Join(newline, lines);
        }
    }
}
