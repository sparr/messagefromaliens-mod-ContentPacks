using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

// The mod's name in glyphs, as game word IDs, for Mod Settings and any other mod that lists mods.
[assembly: AssemblyMetadata("MessageFromAliens.Name", "message de group")]

namespace ContentPacks
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "sparr.messagefromaliens.contentpacks";
        public const string Name = "Content Packs";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> DumpGameText;

        private void Awake()
        {
            Log = Logger;

            DumpGameText = Config.Bind("Content", "DumpGameText", false,
                Described("Write the game's decrypted text files to BepInEx/content/_dump/TxtData/ at startup.", "write game de message"));

            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"{Name} {Version} loaded");
        }

        // Mod Settings shows a setting tagged with its name in glyphs, as game word IDs.
        private static ConfigDescription Described(string description, string glyphs) =>
            new ConfigDescription(description, null, new Dictionary<string, string> { ["MessageFromAliens.Label"] = glyphs });
    }
}
