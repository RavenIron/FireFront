using System.Runtime.CompilerServices;
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

        public static bool DebugEnabled => FireConfig.VerboseLogging != null && FireConfig.VerboseLogging.Value;

        /// <summary>
        /// Only emits when debug logging is on (toggle live with firedebug).
        /// Interpolated calls — the overwhelming majority of the ~100 call
        /// sites — bind to the handler overload below, which makes them truly
        /// FREE when logging is off: the compiler skips all formatting, so no
        /// string is ever built. The old signature allocated the interpolated
        /// string BEFORE the check, at every call, forever; during a big fire
        /// that was hundreds of dead strings a second feeding Mono's GC, and
        /// the collector's periodic ~80ms pause was a visible frametime spike
        /// in a tester's clip (RTX 2060, ~10ms baseline, isolated spikes to
        /// 75-81ms with CPU and GPU both far from saturated).
        /// </summary>
        public static void Debug(string msg)
        {
            if (DebugEnabled) _log?.LogInfo("[debug] " + msg);
        }

        public static void Debug(ref DebugMessage message)
        {
            if (message.Enabled) _log?.LogInfo("[debug] " + message.GetText());
        }

        /// <summary>
        /// Interpolated-string handler for Debug: when DebugEnabled is false
        /// its constructor reports shouldAppend=false and the compiler skips
        /// every literal and every formatted value — zero allocation, zero
        /// ToString calls. Building only happens on the (rare, deliberate)
        /// debug-enabled path.
        /// </summary>
        [InterpolatedStringHandler]
        public ref struct DebugMessage
        {
            private readonly System.Text.StringBuilder _sb;

            public DebugMessage(int literalLength, int formattedCount, out bool shouldAppend)
            {
                shouldAppend = DebugEnabled;
                _sb = shouldAppend ? new System.Text.StringBuilder(literalLength + formattedCount * 16) : null;
            }

            public void AppendLiteral(string value) => _sb.Append(value);

            public void AppendFormatted(string value) => _sb.Append(value);

            public void AppendFormatted<T>(T value) => _sb.Append(value);

            public void AppendFormatted<T>(T value, string format)
            {
                if (value is System.IFormattable formattable) _sb.Append(formattable.ToString(format, null));
                else _sb.Append(value);
            }

            internal bool Enabled => _sb != null;

            internal string GetText() => _sb?.ToString() ?? string.Empty;
        }
    }
}
