using System;
using HarmonyLib;

namespace ContentPacks
{
    // Puts IterativeInit in place of the game's recursive category search and glyph drawing. It
    // is applied only when the packs changed the word table, before Global.Init parses it, so the
    // game's own code runs on its own table.
    internal static class IterativeInitPatches
    {
        private static bool applied;

        internal static void Apply()
        {
            if (applied)
            {
                return;
            }
            applied = true;
            try
            {
                Plugin.Harmony.Patch(AccessTools.Method(typeof(Global), nameof(Global.Search_Radicial)),
                    prefix: new HarmonyMethod(typeof(IterativeInitPatches), nameof(SearchPrefix)));
                Plugin.Harmony.Patch(AccessTools.Method(typeof(AlienInfo), nameof(AlienInfo.GetCharacter)),
                    prefix: new HarmonyMethod(typeof(IterativeInitPatches), nameof(CharacterPrefix)));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Could not replace the game's recursive word table searches, so a very deep table can still overflow while it loads: {e.Message}");
            }
        }

        private static bool SearchPrefix(string a)
        {
            IterativeInit.SearchRadicial(a);
            return false;
        }

        private static void CharacterPrefix(AlienInfo __instance)
        {
            if (__instance.character == null)
            {
                IterativeInit.PrepareCharacter(__instance);
            }
        }
    }
}
