namespace ContentPacks
{
    // Logging for the pack file processors, kept apart from BepInEx so they can be tested alone.
    internal static class Log
    {
        internal static void Debug(string message) => Plugin.Log.LogDebug(message);
        internal static void Info(string message) => Plugin.Log.LogInfo(message);
        internal static void Warning(string message) => Plugin.Log.LogWarning(message);
        internal static void Error(string message) => Plugin.Log.LogError(message);
    }
}
