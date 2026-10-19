using BepInEx.Logging;

namespace COM3D2.DoubleNipple
{
    internal static class Log
    {
        private static ManualLogSource _source;

        // Telemetry is only written when "Verbose Logging" is enabled in the config.
        public static bool VerboseEnabled;

        public static void Init(ManualLogSource source)
        {
            _source = source;
        }

        public static void Info(string message)
        {
            if (_source != null)
            {
                _source.LogInfo(message);
            }
        }

        public static void Warning(string message)
        {
            if (_source != null)
            {
                _source.LogWarning(message);
            }
        }

        public static void Verbose(string message)
        {
            if (VerboseEnabled && _source != null)
            {
                _source.LogInfo("Telemetry " + message);
            }
        }
    }
}
