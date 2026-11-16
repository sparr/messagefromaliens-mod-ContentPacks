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

            try
            {
                if (Plugin.DumpGameText.Value && !dumped)
                {
                    Dump(textfiles);
                    dumped = true;
                }
                if (Plugin.LoadContentPacks.Value)
                {
                    textfiles.TryGetValue(WordTableMerge.Target, out GlyphRenderer.OriginalTable);
                    Apply(textfiles);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Content pack loading failed: {e}");
            }
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

            // Packs apply in ordinal name order; a later pack overrides an earlier one.
            var packs = Directory.GetDirectories(ContentRoot)
                .Where(d => !Path.GetFileName(d).StartsWith("_"))
                .OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal)
                .Select(Classify)
                .Where(p => p != null)
                .ToList();

            // Every replacement first, then every patch, then every merge, so a pack's patches and
            // merges see the files as all packs' replacements left them.
            var categories = Global.alien_message_category;
            foreach (var pack in packs)
            {
                foreach (var (key, file) in pack.Replacements)
                {
                    try
                    {
                        var text = File.ReadAllText(file);
                        var isNew = !textfiles.ContainsKey(key);
                        textfiles[key] = text;

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
                        if (!textfiles.TryGetValue(key, out var text))
                        {
                            Log.Error($"{label}: skipped, because no game file or pack file is named {key}");
                            continue;
                        }
                        var patched = TextPatcher.Apply(text, File.ReadAllText(file), label, Plugin.EnforcePatchContext.Value);
                        if (patched != null)
                        {
                            textfiles[key] = patched;
                            Log.Info($"{label}: patched {key}");
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error($"{label}: skipped, because of an error: {e.Message}");
                    }
                }
            }
            var merges = packs.SelectMany(p => p.Merges.Select(f => ($"[{p.Name}] {Path.GetFileName(f)}", f))).ToList();
            if (merges.Count > 0)
            {
                if (textfiles.TryGetValue(WordTableMerge.Target, out var table))
                {
                    try
                    {
                        textfiles[WordTableMerge.Target] = WordTableMerge.Apply(table, merges, textfiles);
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
                else if (ending == ".patch")
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
