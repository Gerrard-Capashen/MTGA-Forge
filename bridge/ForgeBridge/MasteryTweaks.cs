using System;
using System.Collections;
using UnityEngine;

namespace ForgeBridge
{
    /// <summary>
    /// The mastery track page (server MasteryTrack) borrows a set track's prefab, which has a "Mastery Tree" button (the orb web).
    /// The Forge track has no web: while a RewardTrackView is open, the button is switched off. Offline (no track: the client puts its lock
    /// on the nav bar's Mastery tab, NavBarController.MasteryLock) the tab itself is hidden; the client shows it again on its next refresh.
    /// </summary>
    public static class MasteryTweaks
    {
        /// <summary>The graph id the server borrows (fd.MasteryTrack GRAPH_ID); the page draws only tracks it has art data for.</summary>
        private const string TrackId = "BattlePass_HOB";

        /// <summary>
        /// A client build can drop the art entry of the borrowed set track (build 26149138 has no BattlePass_HOB: "Cannot find track asset data", empty page).
        /// Then a copy of another entry, renamed to the borrowed id, is added to the page's list.
        /// </summary>
        private static void EnsureTrackData(UnityEngine.Object ctl)
        {
            var f = ctl.GetType().GetField("_rewardTrackData", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            var list = f?.GetValue(ctl) as IList;
            if (list == null || list.Count == 0) return;
            FieldInfoHolder.Init(list[0].GetType());
            object source = null;
            var names = new System.Collections.Generic.List<string>();
            foreach (var d in list)
            {
                if (d == null) continue;
                var n = (string)FieldInfoHolder.Name.GetValue(d);
                if (n == TrackId) return;
                names.Add(n);
                // a set track's art over the beginner track (EarlyPlayerProgression)
                if (source == null || (n != null && n.StartsWith("BattlePass_") && !((string)FieldInfoHolder.Name.GetValue(source)).StartsWith("BattlePass_"))) source = d;
            }
            if (source == null) return;
            var copy = FieldInfoHolder.Clone.Invoke(source, null);
            FieldInfoHolder.Name.SetValue(copy, TrackId);
            list.Add(copy);
            Log.Info("MasteryTweaks: no art data for " + TrackId + " (has: " + string.Join(", ", names) + "), borrowing " + FieldInfoHolder.Name.GetValue(source));
            // the page looked the track up on activation, before this ran: open it again now that it has data
            var open = ctl.GetType().GetMethod("OpenTrackView", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (open != null && ((Behaviour)ctl).isActiveAndEnabled) open.Invoke(ctl, new object[] { TrackId });
        }

        private static class FieldInfoHolder
        {
            public static System.Reflection.FieldInfo Name;
            public static System.Reflection.MethodInfo Clone;
            public static void Init(Type t)
            {
                if (Name != null) return;
                Name = t.GetField("TrackName");
                Clone = typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            }
        }

        public static IEnumerator Watch()
        {
            Type view = null, navBar = null, tracks = null;
            while (true)
            {
                yield return new WaitForSeconds(0.3f);
                try
                {
                    if (view == null)
                    {
                        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            view = asm.GetType("RewardTrackView", false);
                            if (view != null) break;
                        }
                        if (view == null) continue;
                    }
                    if (navBar == null) foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) { navBar = asm.GetType("NavBarController", false); if (navBar != null) break; }
                    if (navBar != null)
                    {
                        foreach (var nb in UnityEngine.Object.FindObjectsOfType(navBar))
                        {
                            var lockGo = navBar.GetField("MasteryLock")?.GetValue(nb) as GameObject;
                            var button = navBar.GetField("MasteryButton")?.GetValue(nb) as Component;
                            if (lockGo != null && lockGo.activeSelf && button != null && button.gameObject.activeSelf) button.gameObject.SetActive(false);
                        }
                    }
                    if (tracks == null) foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) { tracks = asm.GetType("ProgressionTracksContentController", false); if (tracks != null) break; }
                    if (tracks != null) foreach (var ctl in UnityEngine.Object.FindObjectsOfType(tracks)) EnsureTrackData(ctl);
                    foreach (var o in UnityEngine.Object.FindObjectsOfType(view))
                    {
                        if (!(o is Component c) || c == null) continue;
                        foreach (var t in c.GetComponentsInChildren<Transform>(false))
                        {
                            if (t.name == "ButtonAnchor") t.gameObject.SetActive(false);
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Error("MasteryTweaks: " + e);
                    yield break;
                }
            }
        }
    }
}
