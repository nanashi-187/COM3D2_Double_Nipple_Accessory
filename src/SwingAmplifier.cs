using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace COM3D2.DoubleNipple
{
    // Exaggerates the swing of legacy (TBoneHair_) chains on nipple accessories.
    //
    // COM3D2's chain constants are fixed in game code, so short accessories swing very little. After
    // the game has posed a chain for the frame, each chain bone's rotation away from its recent average
    // (its settled pose) is multiplied by the configured strength. Only the swing is amplified: the
    // settled hang still comes from the physics, and the simulation itself is never modified, since
    // THair1.Proc re-poses every chain bone from the simulated points on each physics update.
    internal static class SwingAmplifier
    {
        private const float SettleSeconds = 0.4f;
        private const float MaxSwingDegrees = 120f;
        private const int StaleFrames = 600;
        private const int MaxCollisionRetries = 4;
        private const float DepthTolerance = 0.0005f;

        // Pendants hang wider than the chain's centre line (the heart is ~1.8 cm either side), so chain
        // points are tested as small spheres rather than as points.
        private const float AccessoryRadius = 0.01f;

        private static readonly List<Transform> ChainBones = new List<Transform>();

        private static readonly AccessTools.FieldRef<TBoneHair_, List<THair1>> Chains =
            AccessTools.FieldRefAccess<TBoneHair_, List<THair1>>("hair1list");
        private static readonly AccessTools.FieldRef<TBoneHair_, bool> Enabled =
            AccessTools.FieldRefAccess<TBoneHair_, bool>("m_bEnable");

        private sealed class BoneState
        {
            public Transform Bone;
            public Quaternion Settled;
            public int LastFrame;
        }

        private static readonly Dictionary<int, BoneState> Bones = new Dictionary<int, BoneState>();
        private static readonly List<int> StaleIds = new List<int>();

        private static float Strength
        {
            get { return DoubleNipplePlugin.NippleChainSwingStrength; }
        }

        // Accessory 1: the maid's native nipple slots, after TBodySkin.Update ran their physics.
        public static void ApplyToNativeSlots(TBody body)
        {
            if (body == null || Mathf.Approximately(Strength, 1f))
            {
                return;
            }

            ApplyToSlot(body.GetSlot((int)TBody.SlotID.accNipR));
            ApplyToSlot(body.GetSlot((int)TBody.SlotID.accNipL));
        }

        // Accessory 2: called right after the preserved layer's own TBoneHair_.Update.
        public static void Apply(TBoneHair_ hair)
        {
            if (Mathf.Approximately(Strength, 1f) || !SimulatesThisFrame(hair))
            {
                return;
            }

            var chains = Chains(hair);
            if (chains == null)
            {
                return;
            }

            var spheres = hair.bodyhit.spherelist;
            for (var i = 0; i < chains.Count; i++)
            {
                AmplifyChain(chains[i], spheres);
            }
        }

        public static void PruneStale()
        {
            if (Bones.Count == 0)
            {
                return;
            }

            StaleIds.Clear();
            foreach (var pair in Bones)
            {
                if (pair.Value.Bone == null || Time.frameCount - pair.Value.LastFrame > StaleFrames)
                {
                    StaleIds.Add(pair.Key);
                }
            }

            for (var i = 0; i < StaleIds.Count; i++)
            {
                Bones.Remove(StaleIds[i]);
            }
        }

        private static void ApplyToSlot(TBodySkin slot)
        {
            // Same early-outs as TBodySkin.Update: without an active, visible object the game ran no
            // physics, and the bones still hold last frame's amplified pose.
            if (slot == null || slot.obj == null || !slot.boVisible || !slot.obj.activeInHierarchy)
            {
                return;
            }

            Apply(slot.bonehair);
        }

        private static bool SimulatesThisFrame(TBoneHair_ hair)
        {
            return hair != null && hair.bodyhit != null && Enabled(hair);
        }

        // THair1.Proc poses hplist.Count / HairDiv bones, starting at root and following child 0.
        private static void AmplifyChain(THair1 chain, List<THitSphere> spheres)
        {
            if (chain == null || chain.boDBL || chain.root == null || chain.HairDiv <= 0 || chain.hplist == null)
            {
                return;
            }

            // The posed bones plus the end marker below the last one; every point below a bone moves
            // when that bone's swing is amplified.
            ChainBones.Clear();
            var count = chain.hplist.Count / chain.HairDiv;
            var bone = chain.root;
            for (var i = 0; i <= count && bone != null; i++)
            {
                ChainBones.Add(bone);
                bone = bone.childCount > 0 ? bone.GetChild(0) : null;
            }

            var settle = 1f - Mathf.Exp(-Time.deltaTime / SettleSeconds);
            var posed = Mathf.Min(count, ChainBones.Count);
            for (var i = 0; i < posed; i++)
            {
                Amplify(i, settle, spheres);
            }
        }

        private static void Amplify(int index, float settle, List<THitSphere> spheres)
        {
            var bone = ChainBones[index];
            var current = bone.localRotation;
            BoneState state;
            if (!Bones.TryGetValue(bone.GetInstanceID(), out state) || state.Bone != bone)
            {
                state = new BoneState { Bone = bone, Settled = current, LastFrame = -1 };
                Bones[bone.GetInstanceID()] = state;
            }

            // The game can update a body more than once per frame; advance the average only once.
            if (state.LastFrame != Time.frameCount)
            {
                state.Settled = Quaternion.Slerp(state.Settled, current, settle);
                state.LastFrame = Time.frameCount;
            }

            float angle;
            Vector3 axis;
            (Quaternion.Inverse(state.Settled) * current).ToAngleAxis(out angle, out axis);
            if (angle > 180f)
            {
                angle -= 360f;
            }

            if (Mathf.Abs(angle) < 0.01f || float.IsNaN(axis.x))
            {
                return;
            }

            // Never push the chain deeper into the body-collision spheres than the physics pose is:
            // halve the extra swing until it stops making contact worse, or fall back to the physics pose.
            var physicsDepth = Depth(index, spheres);
            var strength = Strength;
            for (var attempt = 0; attempt <= MaxCollisionRetries; attempt++)
            {
                var amplified = Mathf.Clamp(angle * strength, -MaxSwingDegrees, MaxSwingDegrees);
                bone.localRotation = state.Settled * Quaternion.AngleAxis(amplified, axis);
                if (Depth(index, spheres) <= physicsDepth + DepthTolerance)
                {
                    return;
                }

                strength = 1f + (strength - 1f) * 0.5f;
            }

            bone.localRotation = current;
        }

        // Total depth inside the collision spheres of every chain point below the given bone. Radii are
        // scaled like TBodyHit.SphereMove_hair2 and padded by the accessory's own thickness.
        private static float Depth(int index, List<THitSphere> spheres)
        {
            if (spheres == null || spheres.Count == 0)
            {
                return 0f;
            }

            var scale = 1f + _TS.TestVal2 * 2f;
            var total = 0f;
            for (var p = index + 1; p < ChainBones.Count; p++)
            {
                var point = ChainBones[p].position;
                var deepest = 0f;
                for (var s = 0; s < spheres.Count; s++)
                {
                    var sphere = spheres[s];
                    var depth = sphere.len * scale + AccessoryRadius - (point - sphere.wv).magnitude;
                    if (depth > deepest)
                    {
                        deepest = depth;
                    }
                }

                total += deepest;
            }

            return total;
        }
    }
}
