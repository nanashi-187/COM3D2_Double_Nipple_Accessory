using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace COM3D2.DoubleNipple
{
    [BepInPlugin("local.com3d2.doublenipple", "COM3D2 Double Nipple Accessory", "0.6.47")]
    public sealed class DoubleNipplePlugin : BaseUnityPlugin
    {
        private static DoubleNipplePlugin _instance;
        private static ManualLogSource _log;
        private static readonly Dictionary<Maid, List<CloneInfo>> Clones = new Dictionary<Maid, List<CloneInfo>>();
        private static readonly Dictionary<Maid, string> LastPrimary = new Dictionary<Maid, string>();
        private static Maid _pendingMaid;
        private static string _pendingPrimary;
        private static string _pendingClicked;
        private static GameObject _pendingRight;
        private static GameObject _pendingLeft;
        private static bool _pendingQueued;
        private static bool _restoringPrimary;
        private static bool _loadingSavedSecondary;
        private static readonly HashSet<Maid> Rebuilding = new HashSet<Maid>();
        private static readonly string[] TextureProperties =
        {
            "_MainTex", "_ToonRamp", "_ShadowTex", "_ShadowRateToon", "_OutlineTex",
            "_HiTex", "_ToonTex", "_ShadowColorTex", "_DetailTex", "_BumpMap", "_EmissionMap"
        };

        private void Awake()
        {
            _instance = this;
            _log = Logger;
            Harmony.CreateAndPatchAll(typeof(DoubleNipplePlugin));
            Logger.LogInfo("Loaded 0.6.47. Secondary selections reapply after property processing and scene visibility activation.");
        }

        private void Update()
        {
            foreach (var pair in Clones)
            {
                var maid = pair.Key;
                if (maid == null || maid.body0 == null)
                {
                    continue;
                }

            var clones = pair.Value;
            for (var i = 0; i < clones.Count; i++)
            {
                    KeepCloneActive(maid, clones[i]);
            }
            }
        }

        private void LateUpdate()
        {
            foreach (var pair in Clones)
            {
                var clones = pair.Value;
                for (var i = 0; i < clones.Count; i++)
                {
                    if (clones[i] != null && clones[i].Ready && clones[i].BoneHair3Controller != null)
                    {
                        clones[i].BoneHair3Controller.UpdateSelf();
                    }

                    if (clones[i] != null && clones[i].Ready && clones[i].LegacyBoneHairController != null)
                    {
                        clones[i].LegacyBoneHairController.Update();
                    }

                    if (clones[i] != null && clones[i].Ready)
                    {
                        clones[i].SyncBonesFromSource();
                    }
                }
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Maid), nameof(Maid.SetProp), typeof(MPN), typeof(string), typeof(int), typeof(bool), typeof(bool))]
        private static bool SetPropMpn(Maid __instance, MPN idx, string filename)
        {
            if (idx != MPN.accnip)
            {
                return true;
            }

            if (_loadingSavedSecondary)
            {
                return true;
            }

            if (CtrlHeld() && IsDeleteFile(filename) && (HasClone(__instance) || HasSavedSecondary(__instance)))
            {
                ClearClone(__instance);
                ClearSavedSecondary(__instance);
                _log.LogInfo("Telemetry cleared secondary nipple layer via Ctrl+unequip.");
                return false;
            }

            if (CtrlHeld())
            {
                QueueSecondaryAfterGameLoad(__instance, filename);
            }
            else
            {
                RememberPrimary(__instance, filename);
            }

            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Maid), nameof(Maid.SetProp), typeof(string), typeof(string), typeof(int), typeof(bool), typeof(bool))]
        private static bool SetPropString(Maid __instance, string tag, string filename)
        {
            if (tag == null || !tag.Equals("accnip", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (_loadingSavedSecondary)
            {
                return true;
            }

            if (CtrlHeld() && IsDeleteFile(filename) && (HasClone(__instance) || HasSavedSecondary(__instance)))
            {
                ClearClone(__instance);
                ClearSavedSecondary(__instance);
                _log.LogInfo("Telemetry cleared secondary nipple layer via Ctrl+unequip.");
                return false;
            }

            if (CtrlHeld())
            {
                QueueSecondaryAfterGameLoad(__instance, filename);
            }
            else
            {
                RememberPrimary(__instance, filename);
            }

            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Maid), nameof(Maid.DelProp), typeof(MPN), typeof(bool))]
        private static bool ClearSecondary(Maid __instance, MPN idx)
        {
            if (_restoringPrimary)
            {
                _log.LogInfo("Telemetry DelProp during restore ignored by plugin clear path.");
                return true;
            }

            if (idx == MPN.accnip && CtrlHeld() && (HasClone(__instance) || HasSavedSecondary(__instance)))
            {
                ClearClone(__instance);
                ClearSavedSecondary(__instance);
                _log.LogInfo("Telemetry cleared secondary nipple clone layer via Ctrl+DelProp.");
                return false;
            }

            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Maid), nameof(Maid.ResetProp), typeof(MPN), typeof(bool))]
        private static bool ResetSecondary(Maid __instance, MPN idx)
        {
            if (_restoringPrimary)
            {
                _log.LogInfo("Telemetry ResetProp during restore ignored by plugin clear path.");
                return true;
            }

            if (idx == MPN.accnip && CtrlHeld() && (HasClone(__instance) || HasSavedSecondary(__instance)))
            {
                ClearClone(__instance);
                ClearSavedSecondary(__instance);
                _log.LogInfo("Telemetry reset secondary nipple clone layer via Ctrl+ResetProp.");
                return false;
            }

            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TBody), nameof(TBody.LoadBody_R), typeof(string), typeof(Maid))]
        private static void RebuildSecondaryAfterBodyLoad(Maid __1)
        {
            if (_instance != null && __1 != null && !Rebuilding.Contains(__1) && HasSavedSecondary(__1))
            {
                _instance.StartCoroutine(RestoreSavedSecondary(__1));
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Maid), nameof(Maid.AllProcPropSeqStart))]
        private static void RebuildSecondaryAfterPropertyProcessing(Maid __instance)
        {
            if (_instance == null || __instance == null || _loadingSavedSecondary || _restoringPrimary || _pendingQueued || Rebuilding.Contains(__instance) || !HasSavedSecondary(__instance))
            {
                return;
            }

            _instance.StartCoroutine(RestoreAfterExternalPropertyProcessing(__instance));
        }

        private static IEnumerator RestoreAfterExternalPropertyProcessing(Maid maid)
        {
            Rebuilding.Add(maid);
            try
            {
                var waited = 0;
                for (; waited < 300; waited++)
                {
                    if (maid != null && maid.body0 != null && maid.body0.isLoadedBody && !maid.IsAllProcPropBusy)
                    {
                        break;
                    }

                    yield return null;
                }

                if (maid == null || maid.body0 == null || !maid.body0.isLoadedBody || maid.IsAllProcPropBusy)
                {
                    _log.LogInfo("Telemetry external AllProc replay skipped after wait=" + waited + " maid=" + MaidKey(maid));
                    yield break;
                }

                for (var settle = 0; settle < 15; settle++)
                {
                    yield return null;
                }

                var secondary = SavedSecondary(maid);
                if (!string.IsNullOrEmpty(secondary))
                {
                    _log.LogInfo("Telemetry replaying secondary after external AllProc waited=" + waited + " maid=" + MaidKey(maid) + " secondary=" + secondary);
                    BeginSavedSecondaryLoad(maid, secondary);
                }
            }
            finally
            {
                Rebuilding.Remove(maid);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Maid), "set_Visible")]
        private static void RebuildSecondaryAfterVisibilityActivation(Maid __instance, bool value)
        {
            if (!value || _instance == null || __instance == null || _loadingSavedSecondary || _restoringPrimary ||
                _pendingQueued || Rebuilding.Contains(__instance) || !HasSavedSecondary(__instance))
            {
                return;
            }

            _instance.StartCoroutine(RestoreAfterVisibilityActivation(__instance));
        }

        private static IEnumerator RestoreAfterVisibilityActivation(Maid maid)
        {
            Rebuilding.Add(maid);
            try
            {
                var waited = 0;
                for (; waited < 300; waited++)
                {
                    if (maid != null && maid.Visible && maid.body0 != null && maid.body0.isLoadedBody && !maid.IsAllProcPropBusy)
                    {
                        break;
                    }

                    yield return null;
                }

                if (maid == null || !maid.Visible || maid.body0 == null || !maid.body0.isLoadedBody || maid.IsAllProcPropBusy)
                {
                    _log.LogInfo("Telemetry visibility replay skipped after wait=" + waited + " maid=" + MaidKey(maid));
                    yield break;
                }

                for (var settle = 0; settle < 30; settle++)
                {
                    yield return null;
                }

                var secondary = SavedSecondary(maid);
                if (!string.IsNullOrEmpty(secondary) && !_pendingQueued)
                {
                    _log.LogInfo("Telemetry replaying secondary after visibility activation waited=" + waited + " maid=" + MaidKey(maid) + " secondary=" + secondary);
                    BeginSavedSecondaryLoad(maid, secondary);
                }
            }
            finally
            {
                Rebuilding.Remove(maid);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(TBodySkin), nameof(TBodySkin.DeleteObj))]
        private static void LogNippleSlotDelete(TBodySkin __instance)
        {
            if (__instance != null && (__instance.SlotId == TBody.SlotID.accNipR || __instance.SlotId == TBody.SlotID.accNipL))
            {
                var maid = __instance.body != null ? __instance.body.maid : null;
                if (HasSavedSecondary(maid))
                {
                    _log.LogInfo("Telemetry native nipple slot DeleteObj slot=" + __instance.SlotId + " maid=" + MaidKey(maid) + " obj=" + ObjId(__instance.obj));
                }
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TBodySkin), nameof(TBodySkin.Load), typeof(MPN), typeof(Transform), typeof(Transform), typeof(Dictionary<string, Transform>), typeof(string), typeof(string), typeof(string), typeof(string), typeof(int), typeof(bool), typeof(int))]
        private static void LogNippleSlotLoad(TBodySkin __instance, MPN mpn, string filename)
        {
            if (__instance != null && (__instance.SlotId == TBody.SlotID.accNipR || __instance.SlotId == TBody.SlotID.accNipL))
            {
                var maid = __instance.body != null ? __instance.body.maid : null;
                if (HasSavedSecondary(maid))
                {
                    _log.LogInfo("Telemetry native nipple slot Load slot=" + __instance.SlotId + " mpn=" + mpn + " file=" + filename + " maid=" + MaidKey(maid) + " obj=" + ObjId(__instance.obj));
                }
            }
        }

        private static IEnumerator RestoreSavedSecondary(Maid maid)
        {
            Rebuilding.Add(maid);
            try
            {
                for (var frame = 0; frame < 180; frame++)
                {
                    if (maid != null && maid.body0 != null && maid.body0.isLoadedBody && !maid.IsAllProcPropBusy)
                    {
                        break;
                    }

                    yield return null;
                }

                if (maid == null || maid.body0 == null || !maid.body0.isLoadedBody || HasLiveClone(maid))
                {
                    yield break;
                }

                var secondary = SavedSecondary(maid);
                if (string.IsNullOrEmpty(secondary))
                {
                    yield break;
                }

                for (var settle = 0; settle < 30; settle++)
                {
                    yield return null;
                }

                _log.LogInfo("Telemetry rebuilding saved secondary after body load maid=" + MaidKey(maid) + " secondary=" + secondary);
                BeginSavedSecondaryLoad(maid, secondary);
            }
            finally
            {
                Rebuilding.Remove(maid);
            }
        }

        private static void BeginSavedSecondaryLoad(Maid maid, string secondary)
        {
            if (_pendingQueued || maid == null || string.IsNullOrEmpty(secondary))
            {
                return;
            }

            _pendingMaid = maid;
            _pendingPrimary = CurrentNippleFile(maid);
            string remembered;
            if ((string.IsNullOrEmpty(_pendingPrimary) || SameFile(_pendingPrimary, secondary)) && LastPrimary.TryGetValue(maid, out remembered) && !SameFile(remembered, secondary))
            {
                _pendingPrimary = remembered;
            }

            _pendingClicked = secondary;
            _pendingRight = SlotObject(maid, TBody.SlotID.accNipR);
            _pendingLeft = SlotObject(maid, TBody.SlotID.accNipL);
            _pendingQueued = true;
            _instance.StartCoroutine(CloneClickedThenRestorePrimary());

            _loadingSavedSecondary = true;
            try
            {
                maid.SetProp(MPN.accnip, secondary, 0, false, false);
                maid.AllProcPropSeqStart();
            }
            finally
            {
                _loadingSavedSecondary = false;
            }
        }

        private static void QueueSecondaryAfterGameLoad(Maid maid, string filename)
        {
            if (_restoringPrimary || string.IsNullOrEmpty(filename) || filename.IndexOf("_del", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return;
            }

            if (!_pendingQueued && _instance != null)
            {
                _pendingMaid = maid;
                _pendingPrimary = CurrentNippleFile(maid);
                string remembered;
                if (string.IsNullOrEmpty(_pendingPrimary) && LastPrimary.TryGetValue(maid, out remembered))
                {
                    _pendingPrimary = remembered;
                }

                _pendingRight = SlotObject(maid, TBody.SlotID.accNipR);
                _pendingLeft = SlotObject(maid, TBody.SlotID.accNipL);
                _pendingQueued = true;
                _instance.StartCoroutine(CloneClickedThenRestorePrimary());
            }

            _pendingClicked = filename;
            _log.LogInfo("Telemetry queued secondary primary=" + _pendingPrimary + " clicked=" + _pendingClicked + " oldR=" + ObjId(_pendingRight) + " oldL=" + ObjId(_pendingLeft));
        }

        private static void RememberPrimary(Maid maid, string filename)
        {
            if (maid == null || string.IsNullOrEmpty(filename) || filename.IndexOf("_del", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return;
            }

            LastPrimary[maid] = filename;
        }

        private static IEnumerator CloneClickedThenRestorePrimary()
        {
            var maid = _pendingMaid;
            var primary = _pendingPrimary;
            var oldRight = _pendingRight;
            var oldLeft = _pendingLeft;

            yield return null;
            var clicked = _pendingClicked;

            if (maid == null || string.IsNullOrEmpty(clicked))
            {
                ClearPending();
                yield break;
            }

            var waited = 0;
            for (; waited < 90; waited++)
            {
                if (SameFile(CurrentNippleFile(maid), clicked) && !maid.IsAllProcPropBusy && SlotsChanged(maid, oldRight, oldLeft))
                {
                    break;
                }

                yield return null;
            }

            const int settleFrames = 30;
            for (var settle = 0; settle < settleFrames; settle++)
            {
                yield return null;
            }

            _log.LogInfo("Telemetry pre-clone waited=" + waited + " settle=" + settleFrames + " current=" + CurrentNippleFile(maid) + " clicked=" + clicked + " busy=" + maid.IsAllProcPropBusy + " slotR=" + SlotStats(maid, TBody.SlotID.accNipR) + " slotL=" + SlotStats(maid, TBody.SlotID.accNipL));
            if (IsNativeDirectionalPair(maid, oldRight, oldLeft))
            {
                SaveSecondary(maid, clicked);
                _log.LogInfo("Telemetry native directional pair retained without clone or primary restore.");
                ClearPending();
                yield break;
            }

            CloneCurrentNippleSlots(maid, clicked, oldRight, oldLeft);
            SaveSecondary(maid, clicked);
            LogCloneStats("after-clone", maid);
            LogDetailedState("after-clone-detail", maid);

            if (!string.IsNullOrEmpty(primary))
            {
                SetClonesReady(maid);
                const int cloneWarmupFrames = 30;
                for (var warmup = 0; warmup < cloneWarmupFrames; warmup++)
                {
                    yield return null;
                }

                _log.LogInfo("Telemetry clone warmup complete frames=" + cloneWarmupFrames);
                _restoringPrimary = true;
                try
                {
                    maid.SetProp(MPN.accnip, primary, 0, false, false);
                    maid.AllProcPropSeqStart();
                    LastPrimary[maid] = primary;
                    _log.LogInfo("Telemetry restore-issued primary=" + primary + " secondary=" + clicked + " slotR=" + SlotStats(maid, TBody.SlotID.accNipR) + " slotL=" + SlotStats(maid, TBody.SlotID.accNipL));
                }
                finally
                {
                    _restoringPrimary = false;
                }

                if (_instance != null)
                {
                    _instance.StartCoroutine(FinalizeClonesAfterRestore(maid, primary));
                }
            }
            else
            {
                SetClonesReady(maid);
            }

            ClearPending();
        }

        private static IEnumerator WaitForInternalRigsToSettle(Maid maid)
        {
            var transforms = new List<Transform>();
            AddInternalBones(maid, TBody.SlotID.accNipR, transforms);
            AddInternalBones(maid, TBody.SlotID.accNipL, transforms);
            if (transforms.Count == 0)
            {
                yield break;
            }

            var positions = new Vector3[transforms.Count];
            var rotations = new Quaternion[transforms.Count];
            for (var i = 0; i < transforms.Count; i++)
            {
                positions[i] = transforms[i].localPosition;
                rotations[i] = transforms[i].localRotation;
            }

            var stableFrames = 0;
            var waited = 0;
            for (; waited < 240 && stableFrames < 12; waited++)
            {
                yield return null;
                var stable = true;
                for (var i = 0; i < transforms.Count; i++)
                {
                    if (transforms[i] == null)
                    {
                        continue;
                    }

                    if ((transforms[i].localPosition - positions[i]).sqrMagnitude > 0.00000025f
                        || Quaternion.Angle(transforms[i].localRotation, rotations[i]) > 0.25f)
                    {
                        stable = false;
                    }

                    positions[i] = transforms[i].localPosition;
                    rotations[i] = transforms[i].localRotation;
                }

                stableFrames = stable ? stableFrames + 1 : 0;
            }

            _log.LogInfo("Telemetry internal rig settle waited=" + waited + " stableFrames=" + stableFrames + " bones=" + transforms.Count);
        }

        private static void AddInternalBones(Maid maid, TBody.SlotID slotId, List<Transform> transforms)
        {
            var obj = SlotObject(maid, slotId);
            if (obj == null)
            {
                return;
            }

            var renderers = obj.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (var rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                var bones = renderers[rendererIndex].bones;
                for (var boneIndex = 0; boneIndex < bones.Length; boneIndex++)
                {
                    if (bones[boneIndex] != null && bones[boneIndex].IsChildOf(obj.transform) && !transforms.Contains(bones[boneIndex]))
                    {
                        transforms.Add(bones[boneIndex]);
                    }
                }
            }
        }

        private static IEnumerator FinalizeClonesAfterRestore(Maid maid, string primary)
        {
            var waited = 0;
            for (; waited < 90; waited++)
            {
                if (SameFile(CurrentNippleFile(maid), primary) && !maid.IsAllProcPropBusy)
                {
                    break;
                }

                yield return null;
            }

            for (var settle = 0; settle < 3; settle++)
            {
                yield return null;
            }

            SetClonesReady(maid);
            LogCloneStats("after-restore-3f", maid);
            LogDetailedState("after-restore-3f-detail", maid);
            _log.LogInfo("Telemetry live after restore waited=" + waited + " current=" + CurrentNippleFile(maid) + " slotR=" + SlotStats(maid, TBody.SlotID.accNipR) + " slotL=" + SlotStats(maid, TBody.SlotID.accNipL));
        }

        private static void SetClonesReady(Maid maid)
        {
            List<CloneInfo> clones;
            if (!Clones.TryGetValue(maid, out clones))
            {
                return;
            }

            for (var i = 0; i < clones.Count; i++)
            {
                clones[i].Ready = true;
            }
        }

        private static void CloneCurrentNippleSlots(Maid maid, string nextFile, GameObject oldRight, GameObject oldLeft)
        {
            ClearClone(maid);
            if (maid == null || maid.body0 == null)
            {
                return;
            }

            var clones = new List<CloneInfo>();
            var rightChanged = SlotObject(maid, TBody.SlotID.accNipR) != oldRight;
            var leftChanged = SlotObject(maid, TBody.SlotID.accNipL) != oldLeft;
            var cloneBothFallback = !rightChanged && !leftChanged;
            if (rightChanged || cloneBothFallback)
            {
                CloneSlot(maid, maid.body0.GetSlot((int)TBody.SlotID.accNipR), clones, TBody.SlotID.accNipR, maid.body0.transform);
            }

            if (leftChanged || cloneBothFallback)
            {
                CloneSlot(maid, maid.body0.GetSlot((int)TBody.SlotID.accNipL), clones, TBody.SlotID.accNipL, maid.body0.transform);
            }

            Clones[maid] = clones;
            _log.LogInfo("Telemetry cloned clicked nipple layer clones=" + clones.Count + " rightChanged=" + rightChanged + " leftChanged=" + leftChanged + " fallback=" + cloneBothFallback + " next=" + nextFile);
        }

        private static void CloneSlot(Maid maid, TBodySkin slot, List<CloneInfo> clones, TBody.SlotID slotId, Transform stableParent)
        {
            if (slot == null || slot.obj == null)
            {
                _log.LogInfo("Telemetry clone skipped " + slotId + " null-slot-or-obj");
                return;
            }

            var sourceObject = slot.obj;
            var sourceRenderers = sourceObject.GetComponentsInChildren<Renderer>(true);
            var source = sourceObject.transform;
            var clone = sourceObject;
            clone.name = sourceObject.name + "_DoubleNipplePreserved";
            var parent = source.parent != null ? source.parent : stableParent;
            clone.transform.SetParent(parent, false);
            clone.transform.localPosition = source.localPosition;
            clone.transform.localRotation = source.localRotation;
            clone.transform.localScale = source.localScale;
            var transformMap = new Dictionary<Transform, Transform>();
            BuildTransformMap(source, clone.transform, transformMap);

            var renderers = clone.GetComponentsInChildren<Renderer>(true);
            var ownedMeshes = new List<Mesh>();
            var ownedMaterials = new List<Material>();
            var ownedTextures = new List<Texture>();
            var copiedTextures = new Dictionary<Texture, Texture>();
            var sourceBonesToSync = new List<Transform>();
            var cloneBonesToSync = new List<Transform>();
            var internalRigRenderers = 0;
            for (var i = 0; i < renderers.Length; i++)
            {
                renderers[i].enabled = true;
                if (i < sourceRenderers.Length)
                {
                    var sourceMaterials = sourceRenderers[i].sharedMaterials;
                    var materials = new Material[sourceMaterials.Length];
                    for (var materialIndex = 0; materialIndex < sourceMaterials.Length; materialIndex++)
                    {
                        if (sourceMaterials[materialIndex] != null)
                        {
                            materials[materialIndex] = UnityEngine.Object.Instantiate(sourceMaterials[materialIndex]);
                            ownedMaterials.Add(materials[materialIndex]);
                            CopyMaterialTextures(sourceMaterials[materialIndex], materials[materialIndex], copiedTextures, ownedTextures);
                        }
                    }

                    renderers[i].sharedMaterials = materials;
                    var block = new MaterialPropertyBlock();
                    sourceRenderers[i].GetPropertyBlock(block);
                    renderers[i].SetPropertyBlock(block);
                }

                var skinned = renderers[i] as SkinnedMeshRenderer;
                if (skinned != null)
                {
                    var sourceSkinned = i < sourceRenderers.Length ? sourceRenderers[i] as SkinnedMeshRenderer : null;
                    if (sourceSkinned != null)
                    {
                        if (HasInternalBones(sourceSkinned, source))
                        {
                            internalRigRenderers++;
                        }

                        CopySkinBinding(sourceSkinned, skinned, transformMap);
                        var sourceBones = sourceSkinned.bones;
                        for (var boneIndex = 0; boneIndex < sourceBones.Length; boneIndex++)
                        {
                            Transform mappedBone;
                            if (sourceBones[boneIndex] != null && transformMap.TryGetValue(sourceBones[boneIndex], out mappedBone))
                            {
                                sourceBonesToSync.Add(sourceBones[boneIndex]);
                                cloneBonesToSync.Add(mappedBone);
                            }
                        }
                    }

                    if (skinned.sharedMesh != null)
                    {
                        var mesh = UnityEngine.Object.Instantiate(skinned.sharedMesh);
                        skinned.sharedMesh = mesh;
                        ownedMeshes.Add(mesh);
                    }

                    skinned.updateWhenOffscreen = true;
                }
            }

            clone.SetActive(false);
            LogCloneBones(clone);
            var anchor = FindPersistentSkinAnchor(sourceObject, slot.trsBoneAttach) ?? slot.trsBoneAttach;
            BoneHair2 boneHair2 = null;
            BoneHair3 boneHair3 = null;
            TBoneHair_ legacyBoneHair = null;
            if (internalRigRenderers > 0)
            {
                var controllerOwner = new TBodySkin(maid.body0, "DoubleNippleClone", slotId, false);
                boneHair2 = new BoneHair2(controllerOwner);
                if (!boneHair2.InitGameObject(clone, MPN.accnip))
                {
                    boneHair2.Uninit();
                    boneHair2 = null;
                    boneHair3 = new BoneHair3(controllerOwner);
                    if (!boneHair3.InitGameObject(clone, MPN.accnip))
                    {
                        boneHair3.Uninit();
                        boneHair3 = null;
                    }
                }

                legacyBoneHair = new TBoneHair_(controllerOwner);
                legacyBoneHair.SearchGameObj(clone, boneHair3 != null);
            }

            clones.Add(new CloneInfo(clone, sourceObject, anchor, slot.AttachName, slot.AttachSlotIdx, slot.m_vDefScaleLocal, boneHair2, boneHair3, legacyBoneHair, sourceBonesToSync.ToArray(), cloneBonesToSync.ToArray(), slotId, clone.transform.localPosition, clone.transform.localRotation, clone.transform.localScale, clone.transform.position, clone.transform.rotation, ownedMeshes.ToArray(), ownedMaterials.ToArray(), ownedTextures.ToArray()));
            slot.listDEL = new List<UnityEngine.Object>();
            slot.obj = null;
            _log.LogInfo("Telemetry secondary preserved " + slotId + " source=" + ObjId(sourceObject) + " anchor=" + TransformName(anchor) + " attachName=" + slot.AttachName + " attachSlot=" + slot.AttachSlotIdx + " gravity=" + (boneHair2 != null ? "BoneHair2" : boneHair3 != null ? "BoneHair3" : legacyBoneHair != null ? "TBoneHair" : "none") + " renderers=" + renderers.Length + " copiedMeshes=" + ownedMeshes.Count + " internalRigRenderers=" + internalRigRenderers + " copiedMaterials=" + ownedMaterials.Count + " copiedTextures=" + ownedTextures.Count + " propertyBlocks=yes");
        }

        private static Transform FindPersistentSkinAnchor(GameObject sourceObject, Transform preferred)
        {
            if (sourceObject == null)
            {
                return null;
            }

            var sourceRoot = sourceObject.transform;
            var target = preferred != null ? preferred.position : sourceRoot.position;
            Transform closest = null;
            var closestDistance = float.MaxValue;
            var renderers = sourceObject.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (var rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                var bones = renderers[rendererIndex].bones;
                for (var boneIndex = 0; boneIndex < bones.Length; boneIndex++)
                {
                    var bone = bones[boneIndex];
                    if (bone == null || bone == sourceRoot || bone.IsChildOf(sourceRoot))
                    {
                        continue;
                    }

                    var distance = (bone.position - target).sqrMagnitude;
                    if (distance < closestDistance)
                    {
                        closest = bone;
                        closestDistance = distance;
                    }
                }
            }

            return closest;
        }

        private static bool HasInternalBones(SkinnedMeshRenderer renderer, Transform sourceRoot)
        {
            var bones = renderer.bones;
            for (var i = 0; i < bones.Length; i++)
            {
                var current = bones[i];
                while (current != null)
                {
                    if (current == sourceRoot)
                    {
                        return true;
                    }

                    current = current.parent;
                }
            }

            return false;
        }

        private static void BuildTransformMap(Transform source, Transform clone, Dictionary<Transform, Transform> map)
        {
            map[source] = clone;
            var count = Math.Min(source.childCount, clone.childCount);
            for (var i = 0; i < count; i++)
            {
                BuildTransformMap(source.GetChild(i), clone.GetChild(i), map);
            }
        }

        private static void CopySkinBinding(SkinnedMeshRenderer source, SkinnedMeshRenderer clone, Dictionary<Transform, Transform> transformMap)
        {
            var sourceBones = source.bones;
            var cloneBones = new Transform[sourceBones.Length];
            for (var i = 0; i < sourceBones.Length; i++)
            {
                Transform mapped;
                cloneBones[i] = sourceBones[i] != null && transformMap.TryGetValue(sourceBones[i], out mapped) ? mapped : sourceBones[i];
            }

            clone.bones = cloneBones;
            Transform mappedRoot;
            clone.rootBone = source.rootBone != null && transformMap.TryGetValue(source.rootBone, out mappedRoot) ? mappedRoot : source.rootBone;
            clone.localBounds = source.localBounds;

            if (source.sharedMesh != null)
            {
                for (var i = 0; i < source.sharedMesh.blendShapeCount; i++)
                {
                    clone.SetBlendShapeWeight(i, source.GetBlendShapeWeight(i));
                }
            }
        }

        private static void CopyMaterialTextures(Material source, Material destination, Dictionary<Texture, Texture> copied, List<Texture> owned)
        {
            for (var i = 0; i < TextureProperties.Length; i++)
            {
                var property = TextureProperties[i];
                if (!source.HasProperty(property))
                {
                    continue;
                }

                var sourceTexture = source.GetTexture(property);
                if (sourceTexture == null)
                {
                    continue;
                }

                Texture texture;
                if (!copied.TryGetValue(sourceTexture, out texture))
                {
                    texture = UnityEngine.Object.Instantiate(sourceTexture);
                    copied.Add(sourceTexture, texture);
                    owned.Add(texture);
                }

                destination.SetTexture(property, texture);
            }
        }

        private static void LogCloneBones(GameObject clone)
        {
            var cloneRenderers = clone.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (var rendererIndex = 0; rendererIndex < cloneRenderers.Length; rendererIndex++)
            {
                var bones = cloneRenderers[rendererIndex].bones;
                var first = bones.Length > 0 ? TransformName(bones[0]) : "none";
                _log.LogInfo("Telemetry clone bones renderer=" + rendererIndex + " bones=" + bones.Length + " first=" + first + " root=" + TransformName(cloneRenderers[rendererIndex].rootBone));
            }
        }

        private static string TransformName(Transform transform)
        {
            return transform != null ? transform.name : "null";
        }

        private static void KeepCloneActive(Maid maid, CloneInfo info)
        {
            if (info == null || info.Clone == null)
            {
                return;
            }

            if (!info.Ready)
            {
                info.Clone.SetActive(false);
                return;
            }

            var attachPosition = Vector3.zero;
            var attachRotation = Quaternion.identity;
            var attachScale = Vector3.one;
            var usingNativeAttach = !string.IsNullOrEmpty(info.AttachName)
                && info.AttachSlotIndex >= 0
                && info.AttachSlotIndex < maid.body0.goSlot.Count
                && maid.body0.goSlot[info.AttachSlotIndex].morph != null
                && maid.body0.goSlot[info.AttachSlotIndex].morph.GetAttachPoint(info.AttachName, out attachPosition, out attachRotation, out attachScale, false);

            if (usingNativeAttach)
            {
                info.Clone.transform.position = attachPosition;
                info.Clone.transform.rotation = attachRotation;
                info.Clone.transform.localScale = Vector3.Scale(info.DefaultScale, attachScale);
                info.Clone.SetActive(true);
                return;
            }

            GameObject live = null;
            var usingOppositeSlot = false;
            var usingAttachmentPoint = info.AnchorTransform != null;
            if (!usingAttachmentPoint)
            {
                live = SlotObject(maid, info.SlotId);
            }

            if (live == null && !usingAttachmentPoint)
            {
                var opposite = info.SlotId == TBody.SlotID.accNipR ? TBody.SlotID.accNipL : TBody.SlotID.accNipR;
                live = SlotObject(maid, opposite);
                usingOppositeSlot = live != null;
            }

            if (usingAttachmentPoint)
            {
                var deltaRotation = info.AnchorTransform.rotation * Quaternion.Inverse(info.AnchorBaseRotation);
                var cloneTransform = info.Clone.transform;
                cloneTransform.position = info.AnchorTransform.position + deltaRotation * (info.WorldPosition - info.AnchorBasePosition);
                cloneTransform.rotation = deltaRotation * info.WorldRotation;
                cloneTransform.localScale = info.LocalScale;
            }

            if (live != null && live.transform != null)
            {
                var liveTransform = live.transform;
                var cloneTransform = info.Clone.transform;
                if (cloneTransform.parent != liveTransform.parent)
                {
                    cloneTransform.SetParent(liveTransform.parent, false);
                }

                if (!usingOppositeSlot)
                {
                    cloneTransform.localPosition = liveTransform.localPosition;
                    cloneTransform.localRotation = liveTransform.localRotation;
                    cloneTransform.localScale = liveTransform.localScale;
                }
                else if (!info.FollowInitialized && live != info.SourceObject)
                {
                    info.LiveBasePosition = liveTransform.localPosition;
                    info.LiveBaseRotation = liveTransform.localRotation;
                    info.FollowInitialized = true;
                }

                if (usingOppositeSlot && info.FollowInitialized)
                {
                    cloneTransform.localPosition = info.LocalPosition + liveTransform.localPosition - info.LiveBasePosition;
                    cloneTransform.localRotation = liveTransform.localRotation * Quaternion.Inverse(info.LiveBaseRotation) * info.LocalRotation;
                    cloneTransform.localScale = info.LocalScale;
                }
            }

            info.Clone.SetActive(true);
        }

        private static void ClearClone(Maid maid)
        {
            List<CloneInfo> clones;
            if (maid == null || !Clones.TryGetValue(maid, out clones))
            {
                return;
            }

            _log.LogInfo("Telemetry clearing old clones count=" + clones.Count);
            for (var i = 0; i < clones.Count; i++)
            {
                if (clones[i] != null && clones[i].Clone != null)
                {
                    if (clones[i].BoneHair2Controller != null)
                    {
                        clones[i].BoneHair2Controller.Uninit();
                    }

                    if (clones[i].BoneHair3Controller != null)
                    {
                        clones[i].BoneHair3Controller.Uninit();
                    }

                    if (clones[i].LegacyBoneHairController != null)
                    {
                        clones[i].LegacyBoneHairController.Init();
                    }

                    UnityEngine.Object.Destroy(clones[i].Clone);
                }

                if (clones[i] != null && clones[i].OwnedMeshes != null)
                {
                    for (var meshIndex = 0; meshIndex < clones[i].OwnedMeshes.Length; meshIndex++)
                    {
                        if (clones[i].OwnedMeshes[meshIndex] != null)
                        {
                            UnityEngine.Object.Destroy(clones[i].OwnedMeshes[meshIndex]);
                        }
                    }
                }

                if (clones[i] != null && clones[i].OwnedMaterials != null)
                {
                    for (var materialIndex = 0; materialIndex < clones[i].OwnedMaterials.Length; materialIndex++)
                    {
                        if (clones[i].OwnedMaterials[materialIndex] != null)
                        {
                            UnityEngine.Object.Destroy(clones[i].OwnedMaterials[materialIndex]);
                        }
                    }
                }

                if (clones[i] != null && clones[i].OwnedTextures != null)
                {
                    for (var textureIndex = 0; textureIndex < clones[i].OwnedTextures.Length; textureIndex++)
                    {
                        if (clones[i].OwnedTextures[textureIndex] != null)
                        {
                            UnityEngine.Object.Destroy(clones[i].OwnedTextures[textureIndex]);
                        }
                    }
                }

            }

            Clones.Remove(maid);
        }

        private static void LogCloneStats(string label, Maid maid)
        {
            List<CloneInfo> clones;
            if (maid == null || !Clones.TryGetValue(maid, out clones))
            {
                _log.LogInfo("Telemetry " + label + " clones=none");
                return;
            }

            for (var i = 0; i < clones.Count; i++)
            {
                var clone = clones[i].Clone;
                if (clone == null)
                {
                    _log.LogInfo("Telemetry " + label + " clone[" + i + "] destroyed slot=" + clones[i].SlotId);
                    continue;
                }

                var renderers = clone.GetComponentsInChildren<Renderer>(true);
                var enabled = 0;
                for (var r = 0; r < renderers.Length; r++)
                {
                    if (renderers[r].enabled)
                    {
                        enabled++;
                    }
                }

                _log.LogInfo("Telemetry " + label + " clone[" + i + "] slot=" + clones[i].SlotId + " id=" + ObjId(clone) + " active=" + clone.activeSelf + " hierarchy=" + clone.activeInHierarchy + " renderers=" + renderers.Length + " enabled=" + enabled);
            }
        }

        private static void LogDetailedState(string label, Maid maid)
        {
            List<CloneInfo> clones;
            if (maid == null || !Clones.TryGetValue(maid, out clones))
            {
                return;
            }

            for (var i = 0; i < clones.Count; i++)
            {
                var clone = clones[i].Clone;
                if (clone == null)
                {
                    continue;
                }

                LogObjectDetail(label + " clone[" + i + "]", clone);
                var live = SlotObject(maid, clones[i].SlotId);
                LogObjectDetail(label + " live[" + i + "]", live);
            }
        }

        private static void LogObjectDetail(string label, GameObject obj)
        {
            if (obj == null)
            {
                _log.LogInfo("Telemetry " + label + " null");
                return;
            }

            var t = obj.transform;
            _log.LogInfo("Telemetry " + label + " obj=" + ObjId(obj) + " parent=" + ObjId(t.parent != null ? t.parent.gameObject : null) + " layer=" + obj.layer + " pos=" + Vec(t.position) + " local=" + Vec(t.localPosition));
            var renderers = obj.GetComponentsInChildren<Renderer>(true);
            for (var r = 0; r < renderers.Length; r++)
            {
                var renderer = renderers[r];
                var mesh = "-";
                var skinned = renderer as SkinnedMeshRenderer;
                if (skinned != null && skinned.sharedMesh != null)
                {
                    mesh = skinned.sharedMesh.name + "/v=" + skinned.sharedMesh.vertexCount;
                }

                var mat = renderer.sharedMaterial != null ? renderer.sharedMaterial.name + "/" + renderer.sharedMaterial.shader.name : "-";
                _log.LogInfo("Telemetry " + label + " renderer[" + r + "] type=" + renderer.GetType().Name + " enabled=" + renderer.enabled + " visible=" + renderer.isVisible + " layer=" + renderer.gameObject.layer + " bounds.center=" + Vec(renderer.bounds.center) + " bounds.size=" + Vec(renderer.bounds.size) + " mesh=" + mesh + " mat=" + mat);
            }
        }

        private static string Vec(Vector3 value)
        {
            return value.x.ToString("0.000") + "," + value.y.ToString("0.000") + "," + value.z.ToString("0.000");
        }

        private static string CurrentNippleFile(Maid maid)
        {
            var prop = maid.GetProp(MPN.accnip);
            if (prop == null)
            {
                return null;
            }

            return !string.IsNullOrEmpty(prop.strFileName) ? prop.strFileName : prop.strTempFileName;
        }

        private static bool SameFile(string a, string b)
        {
            return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && a.Equals(b, StringComparison.OrdinalIgnoreCase);
        }

        private static bool SlotsChanged(Maid maid, GameObject oldRight, GameObject oldLeft)
        {
            return SlotObject(maid, TBody.SlotID.accNipR) != oldRight || SlotObject(maid, TBody.SlotID.accNipL) != oldLeft;
        }

        private static bool IsNativeDirectionalPair(Maid maid, GameObject oldRight, GameObject oldLeft)
        {
            var right = SlotObject(maid, TBody.SlotID.accNipR);
            var left = SlotObject(maid, TBody.SlotID.accNipL);
            return (oldRight != null && oldLeft == null && right == oldRight && left != null)
                || (oldLeft != null && oldRight == null && left == oldLeft && right != null);
        }

        private static bool HasClone(Maid maid)
        {
            List<CloneInfo> clones;
            return maid != null && Clones.TryGetValue(maid, out clones) && clones.Count > 0;
        }

        private static bool HasLiveClone(Maid maid)
        {
            List<CloneInfo> clones;
            if (maid == null || !Clones.TryGetValue(maid, out clones))
            {
                return false;
            }

            for (var i = 0; i < clones.Count; i++)
            {
                if (clones[i] != null && clones[i].Clone != null)
                {
                    return true;
                }
            }

            return false;
        }

        private static string MaidKey(Maid maid)
        {
            if (maid == null)
            {
                return "unknown";
            }

            object status = null;
            var statusProperty = AccessTools.Property(maid.GetType(), "status");
            if (statusProperty != null)
            {
                status = statusProperty.GetValue(maid, null);
            }
            else
            {
                var statusField = AccessTools.Field(maid.GetType(), "status");
                if (statusField != null)
                {
                    status = statusField.GetValue(maid);
                }
            }

            var raw = "";
            if (status != null)
            {
                var guidProperty = AccessTools.Property(status.GetType(), "guid");
                var guidField = AccessTools.Field(status.GetType(), "guid");
                var value = guidProperty != null ? guidProperty.GetValue(status, null) : guidField != null ? guidField.GetValue(status) : null;
                raw = value != null ? value.ToString() : "";
            }

            if (string.IsNullOrEmpty(raw))
            {
                raw = maid.name;
            }

            var chars = raw.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '-' && chars[i] != '_' && chars[i] != '.')
                {
                    chars[i] = '_';
                }
            }

            return new string(chars);
        }

        private static string SavedSecondary(Maid maid)
        {
            if (_instance == null)
            {
                return "";
            }

            return _instance.Config.Bind("Secondary Accessories", MaidKey(maid), "", "Saved second nipple accessory menu filename.").Value;
        }

        private static bool HasSavedSecondary(Maid maid)
        {
            return !string.IsNullOrEmpty(SavedSecondary(maid));
        }

        private static void SaveSecondary(Maid maid, string filename)
        {
            if (_instance == null || maid == null || string.IsNullOrEmpty(filename))
            {
                return;
            }

            _instance.Config.Bind("Secondary Accessories", MaidKey(maid), "", "Saved second nipple accessory menu filename.").Value = filename;
            _instance.Config.Save();
        }

        private static void ClearSavedSecondary(Maid maid)
        {
            if (_instance == null || maid == null)
            {
                return;
            }

            _instance.Config.Bind("Secondary Accessories", MaidKey(maid), "", "Saved second nipple accessory menu filename.").Value = "";
            _instance.Config.Save();
            Rebuilding.Remove(maid);
        }

        private static GameObject SlotObject(Maid maid, TBody.SlotID slotId)
        {
            if (maid == null || maid.body0 == null)
            {
                return null;
            }

            var slot = maid.body0.GetSlot((int)slotId);
            return slot != null ? slot.obj : null;
        }

        private static string SlotStats(Maid maid, TBody.SlotID slotId)
        {
            var obj = SlotObject(maid, slotId);
            if (obj == null)
            {
                return slotId + "=null";
            }

            var renderers = obj.GetComponentsInChildren<Renderer>(true);
            return slotId + "=" + ObjId(obj) + "/active=" + obj.activeSelf + "/renderers=" + renderers.Length;
        }

        private static string ObjId(GameObject obj)
        {
            return obj == null ? "null" : obj.name + "#" + obj.GetInstanceID();
        }

        private static void ClearPending()
        {
            _pendingMaid = null;
            _pendingPrimary = null;
            _pendingClicked = null;
            _pendingRight = null;
            _pendingLeft = null;
            _pendingQueued = false;
        }

        private static bool CtrlHeld()
        {
            return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        }

        private static bool IsDeleteFile(string filename)
        {
            return !string.IsNullOrEmpty(filename) && filename.IndexOf("_del", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private sealed class CloneInfo
        {
            public readonly GameObject Clone;
            public readonly GameObject SourceObject;
            public readonly Transform AnchorTransform;
            public readonly string AttachName;
            public readonly int AttachSlotIndex;
            public readonly Vector3 DefaultScale;
            public readonly BoneHair2 BoneHair2Controller;
            public readonly BoneHair3 BoneHair3Controller;
            public readonly TBoneHair_ LegacyBoneHairController;
            public readonly Transform[] SourceBones;
            public readonly Transform[] CloneBones;
            public readonly TBody.SlotID SlotId;
            public readonly Vector3 LocalPosition;
            public readonly Quaternion LocalRotation;
            public readonly Vector3 LocalScale;
            public readonly Vector3 WorldPosition;
            public readonly Quaternion WorldRotation;
            public readonly Vector3 AnchorBasePosition;
            public readonly Quaternion AnchorBaseRotation;
            public readonly Mesh[] OwnedMeshes;
            public readonly Material[] OwnedMaterials;
            public readonly Texture[] OwnedTextures;
            public bool FollowInitialized;
            public Vector3 LiveBasePosition;
            public Quaternion LiveBaseRotation;
            public bool Ready;

            public CloneInfo(GameObject clone, GameObject sourceObject, Transform anchorTransform, string attachName, int attachSlotIndex, Vector3 defaultScale, BoneHair2 boneHair2Controller, BoneHair3 boneHair3Controller, TBoneHair_ legacyBoneHairController, Transform[] sourceBones, Transform[] cloneBones, TBody.SlotID slotId, Vector3 localPosition, Quaternion localRotation, Vector3 localScale, Vector3 worldPosition, Quaternion worldRotation, Mesh[] ownedMeshes, Material[] ownedMaterials, Texture[] ownedTextures)
            {
                Clone = clone;
                SourceObject = sourceObject;
                AnchorTransform = anchorTransform;
                AttachName = attachName;
                AttachSlotIndex = attachSlotIndex;
                DefaultScale = defaultScale;
                BoneHair2Controller = boneHair2Controller;
                BoneHair3Controller = boneHair3Controller;
                LegacyBoneHairController = legacyBoneHairController;
                SourceBones = sourceBones;
                CloneBones = cloneBones;
                SlotId = slotId;
                LocalPosition = localPosition;
                LocalRotation = localRotation;
                LocalScale = localScale;
                WorldPosition = worldPosition;
                WorldRotation = worldRotation;
                AnchorBasePosition = anchorTransform != null ? anchorTransform.position : Vector3.zero;
                AnchorBaseRotation = anchorTransform != null ? anchorTransform.rotation : Quaternion.identity;
                OwnedMeshes = ownedMeshes;
                OwnedMaterials = ownedMaterials;
                OwnedTextures = ownedTextures;
            }

            public void SyncBonesFromSource()
            {
                for (var i = 0; i < SourceBones.Length && i < CloneBones.Length; i++)
                {
                    if (SourceBones[i] == null || CloneBones[i] == null)
                    {
                        continue;
                    }

                    CloneBones[i].localPosition = SourceBones[i].localPosition;
                    CloneBones[i].localRotation = SourceBones[i].localRotation;
                    CloneBones[i].localScale = SourceBones[i].localScale;
                }
            }
        }
    }
}
