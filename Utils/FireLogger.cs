using BepInEx.Logging;
using FireFront.Config;

namespace FireFront.Utils
{
    public static class FireLogger
    {
        private static ManualLogSource _log;

        public static void Init(ManualLogSource log) => _log = log;

        public static void Info(string msg) => _log?.LogInfo(msg);

        public static void Warn(string msg) => _log?.LogWarning(msg);

        public static void Error(string msg) => _log?.LogError(msg);

        /// <summary>Only emits when Debug.VerboseLogging is on (toggle live with firedebug).</summary>
        public static void Debug(string msg)
        {
            if (FireConfig.VerboseLogging != null && FireConfig.VerboseLogging.Value)
                _log?.LogInfo($"[debug] {msg}");
        }
    }
}
