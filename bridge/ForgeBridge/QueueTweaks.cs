using System;
using System.Collections;
using System.Reflection;

namespace ForgeBridge
{
    /// <summary>
    /// Fast-forwards the duel's animation queue while stack events are piling up: Vorinclex, Voice of Hunger with 15 lands tapped sends 15
    /// triggers, each its own game state, and the client plays them one after the other. The Patcher calls Tick at the start of
    /// UXEventQueue.Update (scales the timers: stagger, linger) and Move at the start of SplineMovementSystem.UpdateMovement (scales the card
    /// movement). The more stack-bound events wait in the queue the faster it plays; that speed is held until the backlog is gone (recalculated if it grows again), then back to normal.
    /// </summary>
    public static class QueueTweaks
    {
        private const int FreeBacklog = 1;          // this many waiting stack events play at normal speed
        private const float PerEvent = 0.4f;        // extra speed per waiting stack event
        private const float MaxSpeed = 5f;

        private static float speed = 1f;
        private static int lastCount;
        private static bool resolved, broken;
        private static PropertyInfo pendingProp, toZoneProp;
        private static FieldInfo transfersField;
        private static MethodInfo setSpeed, getSpeed;

        public static float Tick(object queue, float dt)
        {
            try
            {
                if (broken) return dt;
                if (!resolved)
                {
                    pendingProp = queue.GetType().GetProperty("PendingEvents", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    resolved = true;
                    if (pendingProp == null) { broken = true; Log.Error("QueueTweaks: UXEventQueue has no PendingEvents"); return dt; }
                }
                int n = 0;
                var pending = pendingProp.GetValue(queue) as IList;
                if (pending != null)
                    for (int i = 0; i < pending.Count; i++)
                        if (HeadsForStack(pending[i])) n++;
                // The speed is set by the backlog when it grows (15 triggers arrive: 5x) and then HELD until the backlog is played out, not
                // eased off as it shrinks. A backlog that grows again (a trigger caused more triggers) recalculates it from the new count.
                if (n <= FreeBacklog) speed = 1f;
                else if (n > lastCount) speed = Math.Min(MaxSpeed, 1f + PerEvent * (n - FreeBacklog));
                lastCount = n;
                return dt * speed;
            }
            catch (Exception e) { broken = true; Log.Error("QueueTweaks.Tick: " + e); return dt; }
        }

        public static void Move(object splines)
        {
            try
            {
                if (broken) return;
                if (setSpeed == null)
                {
                    setSpeed = splines.GetType().GetMethod("SetSpeedModifier", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    getSpeed = splines.GetType().GetMethod("GetSpeedModifier", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (setSpeed == null || getSpeed == null) { broken = true; Log.Error("QueueTweaks: SplineMovementSystem has no SetSpeedModifier"); return; }
                }
                if (speed == 1f && (float)getSpeed.Invoke(splines, null) <= 1f) return;      // not boosted (and nobody else changed it)
                setSpeed.Invoke(splines, new object[] { speed });
            }
            catch (Exception e) { broken = true; Log.Error("QueueTweaks.Move: " + e); }
        }

        /// <summary>A waiting zone transfer (group) whose cards go onto the stack.</summary>
        private static bool HeadsForStack(object evt)
        {
            if (evt == null) return false;
            var t = evt.GetType();
            if (t.Name == "ZoneTransferGroup")
            {
                if (transfersField == null) transfersField = t.GetField("_zoneTransfers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var list = transfersField?.GetValue(evt) as IList;
                return list != null && list.Count > 0 && ToStack(list[0]);
            }
            return t.Name == "ZoneTransferUXEvent" && ToStack(evt);
        }

        private static bool ToStack(object zte)
        {
            if (zte == null) return false;
            if (toZoneProp == null) toZoneProp = zte.GetType().GetProperty("ToZoneType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return toZoneProp?.GetValue(zte)?.ToString() == "Stack";
        }
    }
}
