using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace COM3D2.DoubleNipple
{
    internal static class Patches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Maid), nameof(Maid.SetProp), typeof(MPN), typeof(string), typeof(int), typeof(bool), typeof(bool))]
        private static bool SetPropMpnPrefix(Maid __instance, MPN idx, string filename)
        {
            return idx != MPN.accnip || SecondaryLayerController.OnNippleSetProp(__instance, filename);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Maid), nameof(Maid.SetProp), typeof(string), typeof(string), typeof(int), typeof(bool), typeof(bool))]
        private static bool SetPropStringPrefix(Maid __instance, string tag, string filename)
        {
            var isNipple = tag != null && tag.Equals("accnip", StringComparison.OrdinalIgnoreCase);
            return !isNipple || SecondaryLayerController.OnNippleSetProp(__instance, filename);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Maid), nameof(Maid.DelProp), typeof(MPN), typeof(bool))]
        private static bool DelPropPrefix(Maid __instance, MPN idx)
        {
            return idx != MPN.accnip || SecondaryLayerController.OnNippleCleared(__instance, "DelProp");
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Maid), nameof(Maid.ResetProp), typeof(MPN), typeof(bool))]
        private static bool ResetPropPrefix(Maid __instance, MPN idx)
        {
            return idx != MPN.accnip || SecondaryLayerController.OnNippleCleared(__instance, "ResetProp");
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TBody), nameof(TBody.LoadBody_R), typeof(string), typeof(Maid))]
        private static void LoadBodyPostfix(Maid __1)
        {
            SecondaryLayerController.OnBodyLoaded(__1);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TBody), nameof(TBody.SkinMeshUpdate))]
        private static void SkinMeshUpdatePostfix(TBody __instance)
        {
            SecondaryLayerController.OnSkinMeshUpdated(__instance);
            SwingAmplifier.ApplyToNativeSlots(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TBody), nameof(TBody.WarpInit))]
        private static void WarpInitPostfix(TBody __instance)
        {
            SecondaryLayerController.OnWarpInit(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Maid), nameof(Maid.AllProcPropSeqStart))]
        private static void AllProcPropSeqStartPostfix(Maid __instance)
        {
            SecondaryLayerController.OnPropertyProcessingStarted(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Maid), nameof(Maid.Visible), MethodType.Setter)]
        private static void VisibleSetterPostfix(Maid __instance, bool value)
        {
            if (value)
            {
                SecondaryLayerController.OnBecameVisible(__instance);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(TBodySkin), nameof(TBodySkin.DeleteObj))]
        private static void SlotDeletePrefix(TBodySkin __instance)
        {
            if (Log.VerboseEnabled && Slots.IsNippleSlot(__instance))
            {
                var maid = __instance.body != null ? __instance.body.maid : null;
                Log.Verbose("native nipple slot DeleteObj slot=" + __instance.SlotId + " maid=" + Slots.MaidKey(maid) + " obj=" + Slots.ObjId(__instance.obj));
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TBodySkin), nameof(TBodySkin.Load), typeof(MPN), typeof(Transform), typeof(Transform), typeof(Dictionary<string, Transform>), typeof(string), typeof(string), typeof(string), typeof(string), typeof(int), typeof(bool), typeof(int))]
        private static void SlotLoadPostfix(TBodySkin __instance, MPN mpn, string filename)
        {
            if (Log.VerboseEnabled && Slots.IsNippleSlot(__instance))
            {
                var maid = __instance.body != null ? __instance.body.maid : null;
                Log.Verbose("native nipple slot Load slot=" + __instance.SlotId + " mpn=" + mpn + " file=" + filename + " maid=" + Slots.MaidKey(maid) + " obj=" + Slots.ObjId(__instance.obj));
            }
        }
    }
}
