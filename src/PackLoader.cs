using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using HarmonyLib;

namespace ContentPacks
{
    // Global.Init() decrypts every TxtData asset into Global.textfiles, calls MV.Init(),
    // then parses each name in Global.alien_message_category out of Global.textfiles.
    // A prefix on MV.Init() therefore runs after the game's text is loaded and before it is parsed.
    [HarmonyPatch(typeof(MV), nameof(MV.Init))]
    internal static class PackLoader
    {
        internal const string DumpDirName = "_dump";

        internal static string ContentRoot => Path.Combine(Paths.BepInExRootPath, "content");

        private static bool dumped;

        private static void Prefix()
        {
            var textfiles = Global.textfiles;
            if (textfiles == null)
            {
                Plugin.Log.LogWarning("Global.textfiles is null in MV.Init; skipping content packs");
                return;
            }

            // A failed dump skips only the dump.
            if (Plugin.DumpGameText.Value && !dumped)
            {
                try
                {
                    Dump(textfiles);
                    dumped = true;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Dumping the game's text failed: {e.Message}");
                }
            }
            if (!Plugin.LoadContentPacks.Value)
            {
                return;
            }
            // Global.Init decrypts the game's text again each time it runs, as when the scene
            // reloads, so the packs' result from the first run is put back each time.
            if (result != null)
            {
                Replay(textfiles);
                return;
            }
            var before = new Dictionary<string, string>(textfiles);
            try
            {
                textfiles.TryGetValue(WordTableMerge.Target, out GlyphRenderer.OriginalTable);
                Apply(textfiles);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Content pack loading failed: {e}");
            }
            result = textfiles.Where(kv => !before.TryGetValue(kv.Key, out var old) || old != kv.Value)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            dropped = before.Keys.Where(k => !textfiles.ContainsKey(k)).ToList();
        }

        // The files the packs added or changed, with their text, and the game's files they
        // removed, from the first run.
        private static Dictionary<string, string> result;
        private static List<string> dropped;

        private static void Replay(Dictionary<string, string> textfiles)
        {
            foreach (var kv in result)
            {
                textfiles[kv.Key] = kv.Value;
            }
            foreach (var key in dropped)
            {
                textfiles.Remove(key);
            }
            Log.Debug($"Content packs: {result.Count} changed files put back after the game reloaded its text");
        }

        private static void Dump(Dictionary<string, string> textfiles)
        {
            var dir = Path.Combine(ContentRoot, DumpDirName, "TxtData");
            Directory.CreateDirectory(dir);
            foreach (var kv in textfiles)
            {
                File.WriteAllText(Path.Combine(dir, kv.Key + ".txt"), kv.Value, new UTF8Encoding(false));
            }
            Plugin.Log.LogInfo($"Dumped {textfiles.Count} game text files to {dir}");
        }

        private static void Apply(Dictionary<string, string> textfiles)
        {
            if (!Directory.Exists(ContentRoot))
            {
                Directory.CreateDirectory(ContentRoot);
                return;
            }

            // Packs apply in ordinal name order; a later pack overrides an earlier one. Each has a
            // setting to turn it off, labeled with its name in glyphs when its name is game words.
            var dirs = Directory.GetDirectories(ContentRoot)
                .Where(d => !Path.GetFileName(d).StartsWith("_"))
                .OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal)
                .ToList();
            var words = GameWords(textfiles);
            var packs = new List<Pack>();
            for (var i = 0; i < dirs.Count; i++)
            {
                var name = Path.GetFileName(dirs[i]);
                if (!PackToggles.Enabled(name, i + 1, words))
                {
                    Log.Info($"[{name}] skipped, because it's turned off in the settings");
                    continue;
                }
                var pack = Classify(dirs[i]);
                if (pack != null)
                {
                    packs.Add(pack);
                }
            }

            // Every replacement first, then every patch, then every merge, so a pack's patches and
            // merges see the files as all packs' replacements left them.
            var categories = Global.alien_message_category;
            var stock = new Dictionary<string, string>(textfiles);
            var replaced = new HashSet<string>();
            foreach (var pack in packs)
            {
                foreach (var (name, file) in pack.Replacements)
                {
                    try
                    {
                        var text = File.ReadAllText(file);
                        var key = GameKey(textfiles, name, pack.Name, file);
                        var isNew = !textfiles.ContainsKey(key);
                        textfiles[key] = text;
                        replaced.Add(key);

                        // The game only parses text files named in alien_message_category,
                        // so a new file is registered as a new message category.
                        if (isNew && categories != null && !categories.Contains(key))
                        {
                            categories.Add(key);
                        }
                        Log.Info($"[{pack.Name}] {(isNew ? "added" : "replaced")} {key}");
                    }
                    catch (Exception e)
                    {
                        Log.Error($"[{pack.Name}] {Path.GetFileName(file)}: skipped, because of an error: {e.Message}");
                    }
                }
            }
            foreach (var pack in packs)
            {
                foreach (var (key, file) in pack.Patches)
                {
                    var label = $"[{pack.Name}] {Path.GetFileName(file)}";
                    try
                    {
                        var k = GameKey(textfiles, key, pack.Name, file);
                        if (!textfiles.TryGetValue(k, out var text))
                        {
                            Log.Error($"{label}: skipped, because no game file or pack file is named {key}");
                            continue;
                        }
                        var original = !replaced.Contains(k) && stock.TryGetValue(k, out var stockText) ? stockText : null;
                        var patched = TextPatcher.Apply(text, File.ReadAllText(file), label, Plugin.EnforcePatchContext.Value, original);
                        if (patched != null)
                        {
                            textfiles[k] = patched;
                            Log.Info($"{label}: patched {k}");
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error($"{label}: skipped, because of an error: {e.Message}");
                    }
                }
            }
            CheckText(textfiles, stock, categories);
            // The word table is checked whenever a pack changed it, by replacement, patch, or merge.
            var merges = packs.SelectMany(p => p.Merges.Select(f => ($"[{p.Name}] {Path.GetFileName(f)}", f))).ToList();
            stock.TryGetValue(WordTableMerge.Target, out var stockTable);
            textfiles.TryGetValue(WordTableMerge.Target, out var currentTable);
            if (merges.Count > 0 || currentTable != stockTable)
            {
                if (currentTable != null)
                {
                    try
                    {
                        textfiles[WordTableMerge.Target] = WordTableMerge.Apply(currentTable, stockTable, merges, textfiles);
                    }
                    catch (Exception e)
                    {
                        Log.Error($"{WordTableMerge.Target}: merges skipped, because of an error: {e}");
                    }
                }
                else
                {
                    Log.Error($"{WordTableMerge.Target}: merges skipped, because the game has no word table");
                }
            }
        }

        // The game's file a pack's file is for. Names match regardless of case, as files do on
        // Windows; a pack file named with other letter cases is for the game's own file.
        private static string GameKey(Dictionary<string, string> textfiles, string key, string pack, string file)
        {
            if (textfiles.ContainsKey(key))
            {
                return key;
            }
            var match = textfiles.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                return key;
            }
            Log.Warning($"[{pack}] {Path.GetFileName(file)}: applied to the game's file {match}, whose name differs only in letter case");
            return match;
        }

        // Checks each text file the packs changed, other than the word table, as the game will read
        // it. A file the game can't read is dropped, with the game's own text restored, since one
        // bad line would stop the game while it loads. Blank lines that end a page are removed
        // first.
        private static void CheckText(Dictionary<string, string> textfiles, Dictionary<string, string> stock, List<string> categories)
        {
            foreach (var key in textfiles.Keys.ToList())
            {
                var text = textfiles[key];
                if (key == WordTableMerge.Target || (stock.TryGetValue(key, out var original) && original == text))
                {
                    continue;
                }
                text = GameParse.Tidy(text);
                textfiles[key] = text;
                var problem = GameParse.FileProblem(text, out var line);
                if (problem == null)
                {
                    continue;
                }
                if (stock.TryGetValue(key, out original))
                {
                    textfiles[key] = original;
                    Log.Error($"{key}: as the packs left it, line {line} can't be read: {problem}; the game's own {key} is used");
                }
                else
                {
                    textfiles.Remove(key);
                    categories?.Remove(key);
                    Log.Error($"{key}: line {line} can't be read: {problem}; the file is skipped");
                }
            }
        }

        // The game's words, from its own word table, for naming packs in glyphs.
        private static HashSet<string> GameWords(Dictionary<string, string> textfiles)
        {
            var words = new HashSet<string>();
            var table = textfiles.TryGetValue(WordTableMerge.Target, out var text) ? WordTable.Parse(text, out _) : null;
            if (table != null)
            {
                foreach (var cells in table.Rows)
                {
                    words.Add(cells[table.Columns.WordCol]);
                }
            }
            return words;
        }

        private sealed class Pack
        {
            public string Name;
            public readonly List<(string Key, string File)> Replacements = new List<(string, string)>();
            public readonly List<(string Key, string File)> Patches = new List<(string, string)>();
            public readonly List<string> Merges = new List<string>();
        }

        // Sorts a pack's TxtData files by ending. The target file is the name up to the first dot.
        // <name>.txt replaces it, <name>.patch patches it, and the word table's merges are
        // <table>.csv, applied first, then <table>.<anything>.csv in name order.
        private static Pack Classify(string dir)
        {
            try
            {
                return ClassifyFiles(dir);
            }
            catch (Exception e)
            {
                Log.Error($"[{Path.GetFileName(dir)}]: pack skipped, because its files couldn't be listed: {e.Message}");
                return null;
            }
        }

        private static Pack ClassifyFiles(string dir)
        {
            var pack = new Pack { Name = Path.GetFileName(dir) };
            var txtDir = Path.Combine(dir, "TxtData");
            if (!Directory.Exists(txtDir))
            {
                Log.Warning($"[{pack.Name}]: nothing loaded, because the pack has no TxtData folder");
                return pack;
            }
            var mainMerge = WordTableMerge.Target + ".csv";
            var named = new List<string>();
            foreach (var file in Directory.GetFiles(txtDir).OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                var dot = name.IndexOf('.');
                var key = dot < 0 ? name : name.Substring(0, dot);
                var ending = dot < 0 ? "" : name.Substring(dot);
                if (ending == ".txt")
                {
                    pack.Replacements.Add((key, file));
                }
                else if (ending == ".patch" || name.EndsWith(".patch", StringComparison.Ordinal))
                {
                    pack.Patches.Add((key, file));
                }
                else if (name.EndsWith(".csv", StringComparison.Ordinal))
                {
                    if (key != WordTableMerge.Target)
                    {
                        Log.Error($"[{pack.Name}] {name}: skipped, because only the word table, {WordTableMerge.Target}, takes .csv merges");
                    }
                    else if (name == mainMerge)
                    {
                        pack.Merges.Insert(0, file);
                    }
                    else
                    {
                        named.Add(file);
                    }
                }
                else
                {
                    Log.Warning($"[{pack.Name}] {name}: skipped; pack files end in .txt, .patch, or .csv");
                }
            }
            pack.Merges.AddRange(named);
            return pack;
        }
    }
}
