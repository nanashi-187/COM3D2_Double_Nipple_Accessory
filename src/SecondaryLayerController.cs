using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace COM3D2.DoubleNipple
{
    // Drives the secondary nipple layer: Ctrl+select lets COM3D2 load accessory 2 normally, the loaded
    // objects are preserved, and accessory 1 is restored into the native slots.
    internal static class SecondaryLayerController
    {
        private const int LoadWaitFrames = 90;
        private const int LoadSettleFrames = 30;
        private const int WarmupFrames = 30;
        private const int RestoreWaitFrames = 90;

        private sealed class PendingEquip
        {
            public string Primary;
            public string Clicked;
            public GameObject OldRight;
            public GameObject OldLeft;
        }

        private sealed class MaidState
        {
            public readonly Maid Maid;
            public readonly List<PreservedLayer> Layers = new List<PreservedLayer>();
            public string Guid;
            public string LastPrimary;
            public PendingEquip Pending;
            public bool ReplayScheduled;

            public MaidState(Maid maid)
            {
                Maid = maid;
            }
        }

        // Keyed by instance ID: destroyed Unity objects compare equal to null (and to each other),
        // which makes them unreliable dictionary keys.
        private static readonly Dictionary<int, MaidState> States = new Dictionary<int, MaidState>();
        private static readonly List<int> DeadMaids = new List<int>();

        // > 0 while this plugin itself calls SetProp/AllProcPropSeqStart, so the patches pass through.
        private static int _internalCalls;

        // ---- Harmony entry points ----------------------------------------------------------------

        // Returns false to skip the original SetProp.
        public static bool OnNippleSetProp(Maid maid, string filename)
        {
            if (_internalCalls > 0 || maid == null)
            {
                return true;
            }

            if (!Slots.CtrlHeld())
            {
                RememberPrimary(maid, filename);
                return true;
            }

            if (Slots.IsDeleteFile(filename))
            {
                // Ctrl + "none" removes accessory 2 and leaves accessory 1 equipped.
                return !TryRemoveSecondary(maid, "Ctrl+unequip");
            }

            QueueSecondary(maid, filename);
            return true;
        }

        // DelProp/ResetProp for the nipple property. Returns false to skip the original.
        public static bool OnNippleCleared(Maid maid, string source)
        {
            if (_internalCalls > 0 || maid == null || !Slots.CtrlHeld())
            {
                return true;
            }

            return !TryRemoveSecondary(maid, "Ctrl+" + source);
        }

        public static void OnBodyLoaded(Maid maid)
        {
            ScheduleReplay(maid, "body load", 180, 30, false, false);
        }

        public static void OnPropertyProcessingStarted(Maid maid)
        {
            if (_internalCalls == 0)
            {
                ScheduleReplay(maid, "property processing", 300, 15, false, true);
            }
        }

        public static void OnBecameVisible(Maid maid)
        {
            if (_internalCalls == 0)
            {
                ScheduleReplay(maid, "visibility activation", 300, 30, true, true);
            }
        }

        // ---- Per-frame maintenance ---------------------------------------------------------------

        // Postfix of TBody.SkinMeshUpdate: attach and simulate preserved layers at the same point in
        // the frame as the maid's own slots.
        public static void OnSkinMeshUpdated(TBody body)
        {
            MaidState state;
            var maid = body != null ? body.maid : null;
            if (maid == null || !States.TryGetValue(maid.GetInstanceID(), out state) || state.Layers.Count == 0)
            {
                return;
            }

            for (var i = 0; i < state.Layers.Count; i++)
            {
                state.Layers[i].Tick(maid);
            }
        }

        public static void OnWarpInit(TBody body)
        {
            MaidState state;
            var maid = body != null ? body.maid : null;
            if (maid == null || !States.TryGetValue(maid.GetInstanceID(), out state))
            {
                return;
            }

            for (var i = 0; i < state.Layers.Count; i++)
            {
                state.Layers[i].WarpInit();
            }
        }

        // Destroyed maids: release the preserved objects and resources they still own.
        public static void PruneDestroyedMaids()
        {
            if (States.Count == 0)
            {
                return;
            }

            DeadMaids.Clear();
            foreach (var pair in States)
            {
                if (pair.Value.Maid == null)
                {
                    DeadMaids.Add(pair.Key);
                }
            }

            for (var i = 0; i < DeadMaids.Count; i++)
            {
                DestroyLayers(States[DeadMaids[i]]);
                States.Remove(DeadMaids[i]);
            }
        }

        // ---- State ---------------------------------------------------------------------------------

        private static MaidState GetState(Maid maid)
        {
            MaidState state;
            if (!States.TryGetValue(maid.GetInstanceID(), out state))
            {
                state = new MaidState(maid);
                States[maid.GetInstanceID()] = state;
            }

            // COM3D2 reuses Maid objects for different characters. Drop state that belonged to the
            // previous character so her layer and primary are not applied to the new one.
            var guid = Slots.MaidGuid(maid);
            if (!string.IsNullOrEmpty(guid))
            {
                if (!string.IsNullOrEmpty(state.Guid) && state.Guid != guid && state.Pending == null)
                {
                    Log.Verbose("maid object reassigned " + state.Guid + " -> " + guid + "; clearing previous layer");
                    DestroyLayers(state);
                    state.LastPrimary = null;
                }

                state.Guid = guid;
            }

            return state;
        }

        private static bool HasLiveLayer(MaidState state)
        {
            for (var i = 0; i < state.Layers.Count; i++)
            {
                if (state.Layers[i].Root != null)
                {
                    return true;
                }
            }

            return false;
        }

        private static void DestroyLayers(MaidState state)
        {
            if (state.Layers.Count == 0)
            {
                return;
            }

            Log.Verbose("destroying preserved layers count=" + state.Layers.Count);
            for (var i = 0; i < state.Layers.Count; i++)
            {
                state.Layers[i].Destroy();
            }

            state.Layers.Clear();
        }

        private static void RememberPrimary(Maid maid, string filename)
        {
            if (!string.IsNullOrEmpty(filename) && !Slots.IsDeleteFile(filename))
            {
                GetState(maid).LastPrimary = filename;
            }
        }

        private static bool TryRemoveSecondary(Maid maid, string source)
        {
            var state = GetState(maid);
            if (state.Layers.Count == 0 && !SecondaryStore.Has(maid))
            {
                return false;
            }

            DestroyLayers(state);
            SecondaryStore.Clear(maid);
            Log.Info("Cleared secondary nipple layer via " + source + ".");
            return true;
        }

        // ---- Equip flow ----------------------------------------------------------------------------

        private static void QueueSecondary(Maid maid, string filename)
        {
            if (string.IsNullOrEmpty(filename) || Slots.IsDeleteFile(filename) || DoubleNipplePlugin.Instance == null)
            {
                return;
            }

            var state = GetState(maid);
            if (state.Pending != null)
            {
                // A newer Ctrl-click while the previous one is still loading replaces the target.
                state.Pending.Clicked = filename;
                Log.Verbose("pending secondary retargeted clicked=" + filename);
                return;
            }

            var primary = Slots.CurrentNippleFile(maid);
            if (string.IsNullOrEmpty(primary))
            {
                primary = state.LastPrimary;
            }

            if (Slots.SameFile(primary, filename))
            {
                Log.Info("Ctrl-selected accessory is already the primary; nothing to preserve.");
                return;
            }

            StartEquip(maid, state, primary, filename);
        }

        // Re-applies the saved secondary after COM3D2 rebuilt the maid (scene load, body reload...).
        private static void ReplaySavedSecondary(Maid maid, MaidState state, string reason)
        {
            var secondary = SecondaryStore.Get(maid);
            if (string.IsNullOrEmpty(secondary) || state.Pending != null)
            {
                return;
            }

            var primary = Slots.CurrentNippleFile(maid);
            if ((string.IsNullOrEmpty(primary) || Slots.SameFile(primary, secondary))
                && !string.IsNullOrEmpty(state.LastPrimary) && !Slots.SameFile(state.LastPrimary, secondary))
            {
                primary = state.LastPrimary;
            }

            if (Slots.SameFile(primary, secondary))
            {
                // The native slots already show accessory 2 and there is no other primary to restore;
                // preserving it would only duplicate it.
                Log.Verbose("replay after " + reason + " skipped; secondary is the current primary");
                return;
            }

            Log.Verbose("replaying secondary after " + reason + " maid=" + Slots.MaidKey(maid) + " secondary=" + secondary);
            StartEquip(maid, state, primary, secondary);
            IssueSetProp(maid, secondary);
        }

        private static void StartEquip(Maid maid, MaidState state, string primary, string clicked)
        {
            var pending = new PendingEquip
            {
                Primary = primary,
                Clicked = clicked,
                OldRight = Slots.SlotObject(maid, TBody.SlotID.accNipR),
                OldLeft = Slots.SlotObject(maid, TBody.SlotID.accNipL)
            };

            state.Pending = pending;
            Log.Verbose("queued secondary primary=" + primary + " clicked=" + clicked
                + " oldR=" + Slots.ObjId(pending.OldRight) + " oldL=" + Slots.ObjId(pending.OldLeft));
            DoubleNipplePlugin.Instance.StartCoroutine(PreserveThenRestorePrimary(maid, state, pending));
        }

        private static IEnumerator PreserveThenRestorePrimary(Maid maid, MaidState state, PendingEquip pending)
        {
            try
            {
                // Wait for COM3D2 to finish loading the clicked accessory into the native slots.
                var waited = 0;
                for (; waited < LoadWaitFrames; waited++)
                {
                    yield return null;
                    if (maid == null)
                    {
                        yield break;
                    }

                    if (Slots.SameFile(Slots.CurrentNippleFile(maid), pending.Clicked) && !maid.IsAllProcPropBusy && SlotsChanged(maid, pending))
                    {
                        break;
                    }
                }

                for (var i = 0; i < LoadSettleFrames; i++)
                {
                    yield return null;
                }

                if (maid == null || maid.body0 == null)
                {
                    yield break;
                }

                var clicked = pending.Clicked;
                Log.Verbose("pre-preserve waited=" + waited + " current=" + Slots.CurrentNippleFile(maid) + " clicked=" + clicked
                    + " busy=" + maid.IsAllProcPropBusy + " slotR=" + Slots.SlotStats(maid, TBody.SlotID.accNipR)
                    + " slotL=" + Slots.SlotStats(maid, TBody.SlotID.accNipL));

                if (!SlotsChanged(maid, pending))
                {
                    // Nothing new was loaded; preserving now would steal accessory 1's objects.
                    Log.Warning("Secondary accessory " + clicked + " did not load into the nipple slots; nothing preserved.");
                    yield break;
                }

                if (IsNativeDirectionalPair(maid, pending))
                {
                    SecondaryStore.Set(maid, clicked);
                    Log.Verbose("native directional pair retained without preserving or restoring.");
                    yield break;
                }

                DestroyLayers(state);
                PreserveChangedSlot(maid, state, TBody.SlotID.accNipR, pending.OldRight);
                PreserveChangedSlot(maid, state, TBody.SlotID.accNipL, pending.OldLeft);
                if (state.Layers.Count == 0)
                {
                    Log.Warning("Secondary accessory " + clicked + " had no objects to preserve.");
                    yield break;
                }

                SecondaryStore.Set(maid, clicked);
                SetReady(state);
                LogLayers("after-preserve", maid, state);

                var primary = pending.Primary;
                if (string.IsNullOrEmpty(primary))
                {
                    yield break;
                }

                for (var i = 0; i < WarmupFrames; i++)
                {
                    yield return null;
                }

                if (maid == null)
                {
                    yield break;
                }

                IssueSetProp(maid, primary);
                state.LastPrimary = primary;
                Log.Verbose("restore issued primary=" + primary + " secondary=" + clicked);

                if (Log.VerboseEnabled)
                {
                    DoubleNipplePlugin.Instance.StartCoroutine(LogAfterRestore(maid, state, primary));
                }
            }
            finally
            {
                if (state.Pending == pending)
                {
                    state.Pending = null;
                }
            }
        }

        private static void PreserveChangedSlot(Maid maid, MaidState state, TBody.SlotID slotId, GameObject before)
        {
            if (Slots.SlotObject(maid, slotId) == before)
            {
                return;
            }

            var layer = PreservedLayer.TakeFromSlot(Slots.Slot(maid, slotId));
            if (layer != null)
            {
                state.Layers.Add(layer);
            }
        }

        private static void SetReady(MaidState state)
        {
            for (var i = 0; i < state.Layers.Count; i++)
            {
                state.Layers[i].Ready = true;
            }
        }

        private static void IssueSetProp(Maid maid, string filename)
        {
            _internalCalls++;
            try
            {
                maid.SetProp(MPN.accnip, filename, 0, false, false);
                maid.AllProcPropSeqStart();
            }
            finally
            {
                _internalCalls--;
            }
        }

        private static bool SlotsChanged(Maid maid, PendingEquip pending)
        {
            return Slots.SlotObject(maid, TBody.SlotID.accNipR) != pending.OldRight
                || Slots.SlotObject(maid, TBody.SlotID.accNipL) != pending.OldLeft;
        }

        // A one-sided primary plus the opposite one-sided secondary: COM3D2 already keeps both.
        private static bool IsNativeDirectionalPair(Maid maid, PendingEquip pending)
        {
            var right = Slots.SlotObject(maid, TBody.SlotID.accNipR);
            var left = Slots.SlotObject(maid, TBody.SlotID.accNipL);
            return (pending.OldRight != null && pending.OldLeft == null && right == pending.OldRight && left != null)
                || (pending.OldLeft != null && pending.OldRight == null && left == pending.OldLeft && right != null);
        }

        // ---- Replay scheduling ---------------------------------------------------------------------

        private static void ScheduleReplay(Maid maid, string reason, int maxWaitFrames, int settleFrames, bool requireVisible, bool skipWhilePending)
        {
            if (maid == null || DoubleNipplePlugin.Instance == null || !SecondaryStore.Has(maid))
            {
                return;
            }

            var state = GetState(maid);
            if (state.ReplayScheduled || (skipWhilePending && state.Pending != null))
            {
                return;
            }

            DoubleNipplePlugin.Instance.StartCoroutine(ReplayWhenIdle(maid, state, reason, maxWaitFrames, settleFrames, requireVisible));
        }

        private static IEnumerator ReplayWhenIdle(Maid maid, MaidState state, string reason, int maxWaitFrames, int settleFrames, bool requireVisible)
        {
            state.ReplayScheduled = true;
            try
            {
                var waited = 0;
                for (; waited < maxWaitFrames && !IsReady(maid, requireVisible); waited++)
                {
                    yield return null;
                }

                if (!IsReady(maid, requireVisible))
                {
                    Log.Verbose("replay after " + reason + " skipped; maid not ready after " + waited + " frames");
                    yield break;
                }

                for (var i = 0; i < settleFrames; i++)
                {
                    yield return null;
                }

                if (maid == null || state.Pending != null)
                {
                    yield break;
                }

                if (HasLiveLayer(state))
                {
                    // The preserved layer survived; rebuilding it would only flicker and churn resources.
                    yield break;
                }

                ReplaySavedSecondary(maid, state, reason);
            }
            finally
            {
                state.ReplayScheduled = false;
            }
        }

        private static bool IsReady(Maid maid, bool requireVisible)
        {
            return Slots.IsLoadedAndIdle(maid) && (!requireVisible || maid.Visible);
        }

        // ---- Telemetry -------------------------------------------------------------------------------

        private static IEnumerator LogAfterRestore(Maid maid, MaidState state, string primary)
        {
            var waited = 0;
            for (; waited < RestoreWaitFrames; waited++)
            {
                if (maid == null)
                {
                    yield break;
                }

                if (Slots.SameFile(Slots.CurrentNippleFile(maid), primary) && !maid.IsAllProcPropBusy)
                {
                    break;
                }

                yield return null;
            }

            for (var i = 0; i < 3; i++)
            {
                yield return null;
            }

            if (maid == null)
            {
                yield break;
            }

            LogLayers("after-restore", maid, state);
            Log.Verbose("live after restore waited=" + waited + " current=" + Slots.CurrentNippleFile(maid)
                + " slotR=" + Slots.SlotStats(maid, TBody.SlotID.accNipR) + " slotL=" + Slots.SlotStats(maid, TBody.SlotID.accNipL));
        }

        private static void LogLayers(string label, Maid maid, MaidState state)
        {
            if (!Log.VerboseEnabled)
            {
                return;
            }

            for (var i = 0; i < state.Layers.Count; i++)
            {
                state.Layers[i].LogDetail(label + "[" + i + "] " + state.Layers[i].SlotId, maid);
            }
        }
    }
}
