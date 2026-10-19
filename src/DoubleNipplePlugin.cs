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
        public const string Version = "0.8.0";

        internal static DoubleNipplePlugin Instance { get; private set; }

        // Read every frame, so changes made in-game (e.g. through ConfigurationManager) apply at once.
        internal static bool PreservedLayerBodyCollision
        {
            get { return Instance == null || Instance._bodyCollision == null || Instance._bodyCollision.Value; }
        }

        private ConfigEntry<bool> _verboseLogging;
        private ConfigEntry<bool> _bodyCollision;

        private void Awake()
        {
            Instance = this;
            Log.Init(Logger);

            _verboseLogging = Config.Bind("Debug", "Verbose Logging", false,
                "Write detailed telemetry about nipple slot loads and preserved layers to the BepInEx log, "
                + "including how many chain points of accessory 2 are touching a body-collision sphere.");
            Log.VerboseEnabled = _verboseLogging.Value;
            _verboseLogging.SettingChanged += (sender, args) => Log.VerboseEnabled = _verboseLogging.Value;

            _bodyCollision = Config.Bind("Physics", "Preserved Layer Body Collision", true,
                "Experimental. When off, the legacy (TBoneHair) gravity rig of accessory 2 ignores the body-collision "
                + "spheres, which otherwise damp a chain's motion by up to 80% per frame while it is inside them. "
                + "Chains may then pass into the body. Accessory 1 is not affected.");

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
