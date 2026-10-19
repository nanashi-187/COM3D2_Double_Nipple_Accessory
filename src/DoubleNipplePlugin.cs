using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace COM3D2.DoubleNipple
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class DoubleNipplePlugin : BaseUnityPlugin
    {
        public const string Guid = "local.com3d2.doublenipple";
        public const string Name = "COM3D2 Double Nipple Accessory";
        public const string Version = "0.7.1";

        internal static DoubleNipplePlugin Instance { get; private set; }

        private ConfigEntry<bool> _verboseLogging;

        private void Awake()
        {
            Instance = this;
            Log.Init(Logger);

            _verboseLogging = Config.Bind("Debug", "Verbose Logging", false,
                "Write detailed telemetry about nipple slot loads and preserved layers to the BepInEx log.");
            Log.VerboseEnabled = _verboseLogging.Value;
            _verboseLogging.SettingChanged += (sender, args) => Log.VerboseEnabled = _verboseLogging.Value;

            SecondaryStore.Init(Config);
            Harmony.CreateAndPatchAll(typeof(Patches), Guid);
            Log.Info("Loaded " + Version + ".");
        }

        // Attachment and physics run from the TBody.SkinMeshUpdate patch; this only releases
        // layers whose maid was destroyed.
        private void Update()
        {
            SecondaryLayerController.PruneDestroyedMaids();
        }
    }
}
