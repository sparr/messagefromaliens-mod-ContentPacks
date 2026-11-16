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
        internal static ConfigEntry<bool> LoadContentPacks;
        internal static ConfigEntry<bool> EnforcePatchContext;

        private void Awake()
        {
            Log = Logger;

            LoadContentPacks = Config.Bind("Content", "LoadContentPacks", true,
                Described("Load text files from content packs in BepInEx/content/<pack>/TxtData/. Takes effect at the next launch.", "message de group"));
            DumpGameText = Config.Bind("Content", "DumpGameText", false,
                Described("Write the game's decrypted text files to BepInEx/content/_dump/TxtData/ at startup.", "write game de message"));
            EnforcePatchContext = Config.Bind("Content", "EnforcePatchContext", false,
                Described("Place each patch hunk only where all its context lines match. When off, a hunk that changes lines is placed by its removed lines alone, and its context only chooses between several matches. Takes effect at the next launch.", "near wordline can same"));

            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"{Name} {Version} loaded");
        }

        // Mod Settings shows a setting tagged with its name in glyphs, as game word IDs.
        private static ConfigDescription Described(string description, string glyphs) =>
            new ConfigDescription(description, null, new Dictionary<string, string> { ["MessageFromAliens.Label"] = glyphs });
    }
}
