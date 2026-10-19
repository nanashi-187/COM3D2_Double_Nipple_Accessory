using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace COM3D2.DoubleNipple
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class DoubleNipplePlugin : BaseUnityPlugin
    {
        public const string Guid = "local.com3d2.doublenipple";
        public const string Name = "COM3D2 Double Nipple Accessory";
        public const string Version = "0.7.6";

        internal static DoubleNipplePlugin Instance { get; private set; }

        // Read every frame, so changes made in-game (e.g. through ConfigurationManager) apply at once.
        internal static bool PreservedLayerBodyCollision
        {
            get { return Instance == null || Instance._bodyCollision == null || Instance._bodyCollision.Value; }
        }

        internal static float NippleChainSwingStrength
        {
            get { return Instance == null || Instance._swingStrength == null ? 1f : Instance._swingStrength.Value; }
        }

        internal static float NippleChainSwingTaper
        {
            get { return Instance == null || Instance._swingTaper == null ? 0.5f : Instance._swingTaper.Value; }
        }

        private ConfigEntry<float> _swingTaper;
        private ConfigEntry<bool> _verboseLogging;
        private ConfigEntry<bool> _bodyCollision;
        private ConfigEntry<float> _swingStrength;

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

            _swingStrength = Config.Bind("Physics", "Nipple Chain Swing Strength", 1f,
                new ConfigDescription(
                    "Experimental. Multiplies how far the chains of nipple accessories (accessory 1 and 2) swing away from "
                    + "their settled pose. 1 = the game's own motion. Only legacy (TBoneHair) rigs, i.e. bones named "
                    + "_yure_soft_ / _yure_hard_ / _yure_, are affected.",
                    new AcceptableValueRange<float>(0.5f, 4f)));

            _swingTaper = Config.Bind("Physics", "Nipple Chain Swing Taper", 0.5f,
                new ConfigDescription(
                    "How the extra swing from Swing Strength is spread along a chain. The total stays the same; higher "
                    + "values move it from the top of the chain (under the most tension from the pendant) to the lowest "
                    + "joints. 0 = equal at every joint, 0.5 = linear ramp, 1 = quadratic, 3 = almost all at the last joint.",
                    new AcceptableValueRange<float>(0f, SwingAmplifier.MaxTaper)));

            SecondaryStore.Init(Config);
            Harmony.CreateAndPatchAll(typeof(Patches), Guid);
            Log.Info("Loaded " + Version + ".");
        }

        // Attachment and physics run from the TBody.SkinMeshUpdate patch; this only releases
        // layers whose maid was destroyed.
        private void Update()
        {
            SecondaryLayerController.PruneDestroyedMaids();
            if (Time.frameCount % 300 == 0)
            {
                SwingAmplifier.PruneStale();
            }
        }
    }
}
