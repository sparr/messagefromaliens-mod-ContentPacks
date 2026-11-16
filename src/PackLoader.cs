using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using HarmonyLib;

namespace ContentPacks
{
    // Global.Init() decrypts every TxtData asset into Global.textfiles, then calls MV.Init(),
    // so a prefix on MV.Init() runs once the game's text is loaded.
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
                Plugin.Log.LogWarning("Global.textfiles is null in MV.Init; skipping the dump");
                return;
            }

            try
            {
                if (Plugin.DumpGameText.Value && !dumped)
                {
                    Dump(textfiles);
                    dumped = true;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Dumping the game's text failed: {e}");
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
    }
}
