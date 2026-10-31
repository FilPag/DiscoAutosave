using System.Diagnostics;
using BepInEx.Logging;

namespace DiscoAutosave;

internal static class BuildLog
{
    [Conditional("DISCO_LOGS")]
    internal static void Info(ManualLogSource log, string message) => log.LogInfo(message);
}
