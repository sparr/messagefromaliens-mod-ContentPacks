using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;

namespace ContentPacks
{
    // One setting per content pack, to turn it off. Mod Settings shows each with the pack's name in
    // glyphs: the parts of its folder name between hyphens, when all of them are game words, or
    // otherwise the pack's place in loading order as a number. Packs are read as the game starts,
    // so a change takes effect at the next launch.
    internal static class PackToggles
    {
        internal const string Section = "Packs";

        // The settings page the toggles are on, in glyphs.
        private const string Page = "message group";

        // The plugin's config file. Set by Plugin.
        internal static ConfigFile Config;

        // Whether the pack at this place in loading order, counting from 1, should load. A pack
        // whose name can't be a setting name has no setting and always loads.
        internal static bool Enabled(string pack, int place, ICollection<string> words)
        {
            // BepInEx reads a saved line that starts with # as a comment, so such a setting would
            // never keep its value.
            if (pack.StartsWith("#"))
            {
                Log.Warning($"[{pack}]: always loads, because a setting name can't start with #");
                return true;
            }
            try
            {
                return Config.Bind(Section, pack, true,
                    Plugin.Described($"Load the content pack {pack}. Takes effect at the next launch.", Label(pack, place, words), Page)).Value;
            }
            catch (ArgumentException e)
            {
                Log.Warning($"[{pack}]: always loads, because its name can't be a setting name: {e.Message}");
                return true;
            }
            catch (Exception e) when (Config.TryGetEntry<bool>(Section, pack, out var entry))
            {
                // Bind applies the stored value, then saves the file; only the save failed.
                Log.Warning($"[{pack}]: its setting couldn't be saved: {e.Message}");
                return entry.Value;
            }
        }

        // The pack's name in glyphs: its folder name's hyphen-separated parts, if they are all game
        // words, or its place in loading order.
        internal static string Label(string pack, int place, ICollection<string> words)
        {
            var parts = pack.Split('-');
            return parts.All(p => p.Length > 0 && words.Contains(p)) ? string.Join(" ", parts) : Number(place);
        }

        // A positive number as the game writes it: base 7, one glyph per nonzero digit, written
        // <digit>e<place> above the ones place.
        internal static string Number(int n)
        {
            var tokens = new List<string>();
            for (var place = 0; n > 0; place++, n /= 7)
            {
                var digit = n % 7;
                if (digit > 0)
                {
                    tokens.Insert(0, place == 0 ? digit.ToString() : $"{digit}e{place}");
                }
            }
            return string.Join(" ", tokens);
        }
    }
}
