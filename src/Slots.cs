using System;
using UnityEngine;

namespace COM3D2.DoubleNipple
{
    // Small helpers around COM3D2's nipple property and its right/left body slots.
    internal static class Slots
    {
        public static bool IsNippleSlot(TBodySkin slot)
        {
            return slot != null && (slot.SlotId == TBody.SlotID.accNipR || slot.SlotId == TBody.SlotID.accNipL);
        }

        public static TBody.SlotID Opposite(TBody.SlotID slotId)
        {
            return slotId == TBody.SlotID.accNipR ? TBody.SlotID.accNipL : TBody.SlotID.accNipR;
        }

        public static TBodySkin Slot(Maid maid, TBody.SlotID slotId)
        {
            return maid != null && maid.body0 != null ? maid.body0.GetSlot((int)slotId) : null;
        }

        public static GameObject SlotObject(Maid maid, TBody.SlotID slotId)
        {
            var slot = Slot(maid, slotId);
            return slot != null ? slot.obj : null;
        }

        public static string CurrentNippleFile(Maid maid)
        {
            var prop = maid != null ? maid.GetProp(MPN.accnip) : null;
            if (prop == null)
            {
                return null;
            }

            return !string.IsNullOrEmpty(prop.strFileName) ? prop.strFileName : prop.strTempFileName;
        }

        public static bool SameFile(string a, string b)
        {
            return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && a.Equals(b, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsDeleteFile(string filename)
        {
            return !string.IsNullOrEmpty(filename) && filename.IndexOf("_del", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool CtrlHeld()
        {
            return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        }

        public static bool IsLoadedAndIdle(Maid maid)
        {
            return maid != null && maid.body0 != null && maid.body0.isLoadedBody && !maid.IsAllProcPropBusy;
        }

        public static string MaidGuid(Maid maid)
        {
            if (maid == null)
            {
                return null;
            }

            var status = maid.status;
            return status != null ? status.guid : null;
        }

        // Config key for a maid: her GUID, falling back to the GameObject name.
        public static string MaidKey(Maid maid)
        {
            if (maid == null)
            {
                return "unknown";
            }

            var raw = MaidGuid(maid);
            if (string.IsNullOrEmpty(raw))
            {
                raw = maid.name ?? "unknown";
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

        public static string ObjId(GameObject obj)
        {
            return obj == null ? "null" : obj.name + "#" + obj.GetInstanceID();
        }

        public static string TransformName(Transform transform)
        {
            return transform != null ? transform.name : "null";
        }

        public static string SlotStats(Maid maid, TBody.SlotID slotId)
        {
            var obj = SlotObject(maid, slotId);
            if (obj == null)
            {
                return slotId + "=null";
            }

            return slotId + "=" + ObjId(obj) + "/active=" + obj.activeSelf + "/renderers=" + obj.GetComponentsInChildren<Renderer>(true).Length;
        }
    }
}
