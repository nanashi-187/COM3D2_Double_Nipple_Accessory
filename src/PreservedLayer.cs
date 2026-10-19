using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace COM3D2.DoubleNipple
{
    // A nipple accessory hierarchy that COM3D2 loaded into a slot and that this plugin has taken
    // over, so the slot can be reused for the primary accessory.
    internal sealed class PreservedLayer
    {
        private static readonly string[] TextureProperties =
        {
            "_MainTex", "_ToonRamp", "_ShadowTex", "_ShadowRateToon", "_OutlineTex",
            "_HiTex", "_ToonTex", "_ShadowColorTex", "_DetailTex", "_BumpMap", "_EmissionMap"
        };

        public readonly GameObject Root;
        public readonly TBody.SlotID SlotId;
        public bool Ready;

        private readonly string _attachName;
        private readonly int _attachSlotIndex;
        private readonly Vector3 _defaultScale;

        private readonly Transform _anchor;
        private readonly Vector3 _anchorBasePosition;
        private readonly Quaternion _anchorBaseRotation;

        private readonly Vector3 _localPosition;
        private readonly Quaternion _localRotation;
        private readonly Vector3 _localScale;
        private readonly Vector3 _worldPosition;
        private readonly Quaternion _worldRotation;

        private int _lastTickFrame = -1;
        private bool _followInitialized;
        private Vector3 _liveBasePosition;
        private Quaternion _liveBaseRotation;

        private const int ContactLogIntervalFrames = 120;
        private static readonly AccessTools.FieldRef<TBoneHair_, List<THair1>> HairChains =
            AccessTools.FieldRefAccess<TBoneHair_, List<THair1>>("hair1list");

        private BoneHair2 _boneHair2;
        private BoneHair3 _boneHair3;
        private TBoneHair_ _legacyBoneHair;

        // Stand-in with no spheres, swapped in while "Preserved Layer Body Collision" is off. The real
        // TBodyHit is shared with the slot's own controller, so it is never modified.
        private TBodyHit _noCollision;
        private int _contactFrames;
        private int _contactPoints;
        private int _chainPoints;

        // Copies made by this plugin, plus the slot's original deletion list (meshes, materials and
        // textures COM3D2 created for this accessory). Both are destroyed together with the layer.
        private readonly List<Object> _owned;
        private readonly List<Object> _inherited;

        private PreservedLayer(TBodySkin slot, GameObject root, Transform anchor, List<Object> owned, List<Object> inherited)
        {
            Root = root;
            SlotId = slot.SlotId;
            _attachName = slot.AttachName;
            _attachSlotIndex = slot.AttachSlotIdx;
            _defaultScale = slot.m_vDefScaleLocal;

            _anchor = anchor;
            _anchorBasePosition = anchor != null ? anchor.position : Vector3.zero;
            _anchorBaseRotation = anchor != null ? anchor.rotation : Quaternion.identity;

            var t = root.transform;
            _localPosition = t.localPosition;
            _localRotation = t.localRotation;
            _localScale = t.localScale;
            _worldPosition = t.position;
            _worldRotation = t.rotation;

            _owned = owned;
            _inherited = inherited;
        }

        // Which of the adopted controllers is actually driving this accessory (for telemetry).
        public string GravityName
        {
            get
            {
                var names = new List<string>();
                if (_boneHair2 != null && Traverse.Create(_boneHair2).Field("m_db").GetValue() != null)
                {
                    names.Add("BoneHair2");
                }

                if (_boneHair3 != null && Traverse.Create(_boneHair3).Field("m_SkirtBone").GetValue() != null)
                {
                    names.Add("BoneHair3");
                }

                if (_legacyBoneHair != null && Traverse.Create(_legacyBoneHair).Field("m_bEnable").GetValue<bool>())
                {
                    names.Add(_legacyBoneHair.bodyhit != null ? "TBoneHair" : "TBoneHair(no body-hit)");
                }

                return names.Count > 0 ? string.Join("+", names.ToArray()) : "none";
            }
        }

        // Takes ownership of the slot's current object. On success the slot is left empty
        // (obj == null, empty deletion list, fresh gravity controllers) so COM3D2 can load another
        // accessory into it.
        public static PreservedLayer TakeFromSlot(TBodySkin slot)
        {
            if (slot == null || slot.obj == null)
            {
                return null;
            }

            var root = slot.obj;
            root.name += "_DoubleNipplePreserved";

            var owned = new List<Object>();
            var copiedTextures = new Dictionary<Texture, Texture>();
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                renderer.enabled = true;
                renderer.sharedMaterials = CopyMaterials(renderer.sharedMaterials, copiedTextures, owned);

                var skinned = renderer as SkinnedMeshRenderer;
                if (skinned != null)
                {
                    CopyMesh(skinned, owned);
                    skinned.updateWhenOffscreen = true;
                }
            }

            root.SetActive(false);
            var anchor = FindPersistentSkinAnchor(root, slot.trsBoneAttach) ?? slot.trsBoneAttach;
            var layer = new PreservedLayer(slot, root, anchor, owned, slot.listDEL ?? new List<Object>());
            layer.AdoptPhysics(slot);

            slot.listDEL = new List<Object>();
            slot.obj = null;

            Log.Verbose("preserved " + slot.SlotId + " obj=" + Slots.ObjId(root) + " anchor=" + Slots.TransformName(anchor)
                + " attachName=" + layer._attachName + " attachSlot=" + layer._attachSlotIndex + " gravity=" + layer.GravityName
                + " renderers=" + renderers.Length + " owned=" + owned.Count + " inherited=" + layer._inherited.Count);
            return layer;
        }

        // COM3D2 already set up the slot's gravity controllers for this accessory while loading it,
        // including the body-hit spheres the legacy rig needs (TBoneHair_.Update does nothing without
        // them). Take those controllers over and give the slot fresh ones for accessory 1, so the
        // slot's cleanup on restore does not tear down the controllers driving this layer.
        private void AdoptPhysics(TBodySkin slot)
        {
            _boneHair2 = slot.bonehair2;
            _boneHair3 = slot.bonehair3;
            _legacyBoneHair = slot.bonehair;

            // Body-hit data is loaded once per body (TBodySkin.LoadHitcheckData) and resized by the
            // breast sliders, so the slot's new controller shares the same TBodyHit instance.
            var fresh = new TBoneHair_(slot);
            fresh.Init();
            fresh.bodyhit = _legacyBoneHair != null ? _legacyBoneHair.bodyhit : null;
            slot.bonehair = fresh;
            slot.bonehair2 = new BoneHair2(slot);
            slot.bonehair3 = new BoneHair3(slot);
        }

        // Called right after the maid's TBody.SkinMeshUpdate, which is where COM3D2 attaches its own
        // slots and runs their gravity: after animation, IK, breast jiggle and auto-twist.
        public void Tick(Maid maid)
        {
            // SkinMeshUpdate can run more than once in a frame; physics must only advance once.
            if (_lastTickFrame == Time.frameCount)
            {
                return;
            }

            _lastTickFrame = Time.frameCount;
            FollowBody(maid);
            UpdatePhysics(maid);
        }

        private void FollowBody(Maid maid)
        {
            if (Root == null)
            {
                return;
            }

            if (!Ready)
            {
                if (Root.activeSelf)
                {
                    Root.SetActive(false);
                }

                return;
            }

            var t = Root.transform;
            Vector3 attachPosition;
            Quaternion attachRotation;
            Vector3 attachScale;
            if (TryGetNativeAttachPoint(maid, out attachPosition, out attachRotation, out attachScale))
            {
                t.position = attachPosition;
                t.rotation = attachRotation;
                t.localScale = Vector3.Scale(_defaultScale, attachScale);
            }
            else if (_anchor != null)
            {
                var deltaRotation = _anchor.rotation * Quaternion.Inverse(_anchorBaseRotation);
                t.position = _anchor.position + deltaRotation * (_worldPosition - _anchorBasePosition);
                t.rotation = deltaRotation * _worldRotation;
                t.localScale = _localScale;
            }
            else
            {
                FollowLiveSlot(maid, t);
            }

            if (!Root.activeSelf)
            {
                Root.SetActive(true);
            }
        }

        private bool TryGetNativeAttachPoint(Maid maid, out Vector3 position, out Quaternion rotation, out Vector3 scale)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            scale = Vector3.one;
            if (string.IsNullOrEmpty(_attachName) || _attachSlotIndex < 0 || _attachSlotIndex >= maid.body0.goSlot.Count)
            {
                return false;
            }

            var morph = maid.body0.goSlot[_attachSlotIndex].morph;
            return morph != null && morph.GetAttachPoint(_attachName, out position, out rotation, out scale, false);
        }

        // Fallback for accessories without attach metadata or an external skin bone: copy the live
        // primary in the same slot, or keep our original offset relative to the opposite slot.
        private void FollowLiveSlot(Maid maid, Transform t)
        {
            var live = Slots.SlotObject(maid, SlotId);
            var usingOpposite = false;
            if (live == null)
            {
                live = Slots.SlotObject(maid, Slots.Opposite(SlotId));
                usingOpposite = live != null;
            }

            if (live == null)
            {
                return;
            }

            var liveTransform = live.transform;
            if (t.parent != liveTransform.parent)
            {
                t.SetParent(liveTransform.parent, false);
            }

            if (!usingOpposite)
            {
                t.localPosition = liveTransform.localPosition;
                t.localRotation = liveTransform.localRotation;
                t.localScale = liveTransform.localScale;
                return;
            }

            if (!_followInitialized)
            {
                _liveBasePosition = liveTransform.localPosition;
                _liveBaseRotation = liveTransform.localRotation;
                _followInitialized = true;
            }

            t.localPosition = _localPosition + liveTransform.localPosition - _liveBasePosition;
            t.localRotation = liveTransform.localRotation * Quaternion.Inverse(_liveBaseRotation) * _localRotation;
            t.localScale = _localScale;
        }

        // Mirrors the end of TBodySkin.Update. BoneHair2 needs no call here: its DynamicBone
        // component updates itself.
        private void UpdatePhysics(Maid maid)
        {
            if (!Ready || Root == null || !Root.activeInHierarchy || (_boneHair3 == null && _legacyBoneHair == null))
            {
                return;
            }

            // The rigs are tuned for an unscaled maid, so COM3D2 runs them with the maid's root and
            // offset scale temporarily reset to 1 (same tolerance and x-axis check as the game).
            var maidTransform = maid.transform;
            var offsetTransform = maid.m_goOffset != null ? maid.m_goOffset.transform : null;
            var maidScale = maidTransform.localScale;
            var offsetScale = offsetTransform != null ? offsetTransform.localScale : Vector3.one;
            var normalize = !IsUnitScale(maidScale.x) || !IsUnitScale(offsetScale.x);
            try
            {
                if (normalize)
                {
                    maidTransform.localScale = Vector3.one;
                    if (offsetTransform != null)
                    {
                        offsetTransform.localScale = Vector3.one;
                    }
                }

                if (_legacyBoneHair != null)
                {
                    UpdateLegacyRig();
                }

                if (_boneHair3 != null)
                {
                    _boneHair3.UpdateSelf();
                }
            }
            catch (Exception ex)
            {
                // Disable the rig instead of throwing every frame.
                Log.Warning("Physics update failed for preserved " + SlotId + "; disabling its gravity rig. " + ex.Message);
                _boneHair3 = null;
                _legacyBoneHair = null;
            }
            finally
            {
                if (normalize)
                {
                    maidTransform.localScale = maidScale;
                    if (offsetTransform != null)
                    {
                        offsetTransform.localScale = offsetScale;
                    }
                }
            }
        }

        private void UpdateLegacyRig()
        {
            var bodyHit = _legacyBoneHair.bodyhit;
            var bypass = bodyHit != null && !DoubleNipplePlugin.PreservedLayerBodyCollision;
            if (bypass)
            {
                if (_noCollision == null)
                {
                    _noCollision = new TBodyHit();
                }

                // Proc() reads RotOffset for the chain's orientation, so it must match the real data.
                _noCollision.RotOffset = bodyHit.RotOffset;
                _legacyBoneHair.bodyhit = _noCollision;
            }

            try
            {
                _legacyBoneHair.Update();
            }
            finally
            {
                if (bypass)
                {
                    _legacyBoneHair.bodyhit = bodyHit;
                }
            }

            SwingAmplifier.Apply(_legacyBoneHair);

            if (Log.VerboseEnabled)
            {
                CountContacts(bypass);
            }
        }

        // THp.Secchi is set by TBodyHit.SphereMove_hair2 when a point is inside a collision sphere,
        // which is also when its velocity gets damped.
        private void CountContacts(bool collisionBypassed)
        {
            var chains = HairChains(_legacyBoneHair);
            if (chains == null)
            {
                return;
            }

            for (var c = 0; c < chains.Count; c++)
            {
                var points = chains[c].hplist;
                for (var p = 1; p < points.Count; p++)
                {
                    _chainPoints++;
                    if (points[p].Secchi)
                    {
                        _contactPoints++;
                    }
                }
            }

            if (++_contactFrames < ContactLogIntervalFrames)
            {
                return;
            }

            var perFrame = _chainPoints / (float)_contactFrames;
            var touching = _contactPoints / (float)_contactFrames;
            Log.Verbose("contact " + SlotId + ": " + touching.ToString("0.0") + " of " + perFrame.ToString("0")
                + " chain points touching body-collision spheres (avg over " + _contactFrames + " frames, collision "
                + (collisionBypassed ? "OFF" : "on") + ")");
            _contactFrames = 0;
            _contactPoints = 0;
            _chainPoints = 0;
        }

        private static bool IsUnitScale(float value)
        {
            return value >= 0.998f && value < 1.002f;
        }

        // Called after TBody.WarpInit (teleports, scene placement) so the skirt-style rig snaps to its
        // new position instead of swinging across the jump, as the maid's own slots do.
        public void WarpInit()
        {
            var boneHair3 = _boneHair3;
            if (boneHair3 != null)
            {
                TryRun(() => boneHair3.WarpInit());
            }
        }

        public void Destroy()
        {
            TryRun(() => { if (_boneHair2 != null) _boneHair2.Uninit(); });
            TryRun(() => { if (_boneHair3 != null) _boneHair3.Uninit(); });
            TryRun(() => { if (_legacyBoneHair != null) _legacyBoneHair.Init(); });
            _boneHair2 = null;
            _boneHair3 = null;
            _legacyBoneHair = null;

            var rootTransform = Root != null ? Root.transform : null;
            for (var i = 0; i < _inherited.Count; i++)
            {
                var obj = _inherited[i];
                if (obj != null && !_owned.Contains(obj) && !IsInHierarchy(obj, rootTransform))
                {
                    Object.Destroy(obj);
                }
            }

            for (var i = 0; i < _owned.Count; i++)
            {
                if (_owned[i] != null)
                {
                    Object.Destroy(_owned[i]);
                }
            }

            _inherited.Clear();
            _owned.Clear();

            if (Root != null)
            {
                Object.Destroy(Root);
            }
        }

        public void LogDetail(string label, Maid maid)
        {
            if (!Log.VerboseEnabled)
            {
                return;
            }

            LogObjectDetail(label + " preserved", Root);
            LogObjectDetail(label + " live", Slots.SlotObject(maid, SlotId));
        }

        private static void LogObjectDetail(string label, GameObject obj)
        {
            if (obj == null)
            {
                Log.Verbose(label + " null");
                return;
            }

            var t = obj.transform;
            Log.Verbose(label + " obj=" + Slots.ObjId(obj) + " parent=" + Slots.ObjId(t.parent != null ? t.parent.gameObject : null)
                + " active=" + obj.activeSelf + " hierarchy=" + obj.activeInHierarchy + " layer=" + obj.layer
                + " pos=" + Vec(t.position) + " local=" + Vec(t.localPosition));
            var renderers = obj.GetComponentsInChildren<Renderer>(true);
            for (var r = 0; r < renderers.Length; r++)
            {
                var renderer = renderers[r];
                var skinned = renderer as SkinnedMeshRenderer;
                var mesh = skinned != null && skinned.sharedMesh != null ? skinned.sharedMesh.name + "/v=" + skinned.sharedMesh.vertexCount : "-";
                var mat = renderer.sharedMaterial != null ? renderer.sharedMaterial.name + "/" + renderer.sharedMaterial.shader.name : "-";
                Log.Verbose(label + " renderer[" + r + "] type=" + renderer.GetType().Name + " enabled=" + renderer.enabled
                    + " visible=" + renderer.isVisible + " bounds.center=" + Vec(renderer.bounds.center)
                    + " bounds.size=" + Vec(renderer.bounds.size) + " mesh=" + mesh + " mat=" + mat);
            }
        }

        private static string Vec(Vector3 value)
        {
            return value.x.ToString("0.000") + "," + value.y.ToString("0.000") + "," + value.z.ToString("0.000");
        }

        private static Material[] CopyMaterials(Material[] source, Dictionary<Texture, Texture> copiedTextures, List<Object> owned)
        {
            var materials = new Material[source.Length];
            for (var i = 0; i < source.Length; i++)
            {
                if (source[i] == null)
                {
                    continue;
                }

                materials[i] = Object.Instantiate(source[i]);
                owned.Add(materials[i]);
                CopyMaterialTextures(source[i], materials[i], copiedTextures, owned);
            }

            return materials;
        }

        private static void CopyMaterialTextures(Material source, Material destination, Dictionary<Texture, Texture> copied, List<Object> owned)
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
                    texture = Object.Instantiate(sourceTexture);
                    copied.Add(sourceTexture, texture);
                    owned.Add(texture);
                }

                destination.SetTexture(property, texture);
            }
        }

        private static void CopyMesh(SkinnedMeshRenderer skinned, List<Object> owned)
        {
            var source = skinned.sharedMesh;
            if (source == null)
            {
                return;
            }

            var weights = new float[source.blendShapeCount];
            for (var i = 0; i < weights.Length; i++)
            {
                weights[i] = skinned.GetBlendShapeWeight(i);
            }

            var mesh = Object.Instantiate(source);
            skinned.sharedMesh = mesh;
            owned.Add(mesh);

            for (var i = 0; i < weights.Length && i < mesh.blendShapeCount; i++)
            {
                skinned.SetBlendShapeWeight(i, weights[i]);
            }
        }

        // The skin bone outside the accessory's own hierarchy that is closest to its attach point.
        private static Transform FindPersistentSkinAnchor(GameObject root, Transform preferred)
        {
            var rootTransform = root.transform;
            var target = preferred != null ? preferred.position : rootTransform.position;
            Transform closest = null;
            var closestDistance = float.MaxValue;
            var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (var r = 0; r < renderers.Length; r++)
            {
                var bones = renderers[r].bones;
                for (var b = 0; b < bones.Length; b++)
                {
                    var bone = bones[b];
                    if (bone == null || bone.IsChildOf(rootTransform))
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

        private static bool IsInHierarchy(Object obj, Transform root)
        {
            if (root == null)
            {
                return false;
            }

            var go = obj as GameObject;
            if (go != null)
            {
                return go.transform.IsChildOf(root);
            }

            var component = obj as Component;
            return component != null && component.transform.IsChildOf(root);
        }

        private static void TryRun(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Warning("Cleanup step failed: " + ex.Message);
            }
        }
    }
}
