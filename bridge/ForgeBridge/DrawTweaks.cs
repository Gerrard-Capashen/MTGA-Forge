using System;
using System.Collections;
using System.Reflection;

namespace ForgeBridge
{
    /// <summary>
    /// Big draws (a mulligan's new hand, Windfall...) animate card by card: ZoneTransferGroup staggers the cards of a group and caps
    /// the whole stagger at MAX_STAGGER_TIME (1.5 s, so a 7-card hand takes 1.5 s to deal). The Patcher calls Apply at the start of
    /// ZoneTransferGroup.Update; a group of 5 or more draws gets a shorter cap, still one card after the other but a lot faster.
    /// </summary>
    public static class DrawTweaks
    {
        private const int MinCards = 5;
        private const float FastStagger = 0.5f;

        private static FieldInfo capField, transfersField;
        private static bool resolved;

        public static void Apply(object group)
        {
            try
            {
                if (!resolved)
                {
                    var t = group.GetType();
                    capField = t.GetField("MAX_STAGGER_TIME", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    transfersField = t.GetField("_zoneTransfers", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    resolved = true;
                    if (capField == null || transfersField == null) Log.Error("DrawTweaks: ZoneTransferGroup has no MAX_STAGGER_TIME / _zoneTransfers");
                }
                if (capField == null || transfersField == null) return;
                if ((float)capField.GetValue(group) <= FastStagger) return;
                var list = transfersField.GetValue(group) as IList;
                if (list == null || list.Count < MinCards) return;
                foreach (var zt in list)
                {
                    var r = ReasonOf(zt);
                    if (r == null || r != "Draw") return;
                }
                capField.SetValue(group, FastStagger);
            }
            catch (Exception e) { Log.Error("DrawTweaks.Apply: " + e); }
        }

        private static string ReasonOf(object zt)
        {
            if (zt == null) return null;
            var t = zt.GetType();
            var p = t.GetProperty("Reason", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (p != null) return p.GetValue(zt)?.ToString();
            var f = t.GetField("Reason", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return f?.GetValue(zt)?.ToString();
        }
    }
}
