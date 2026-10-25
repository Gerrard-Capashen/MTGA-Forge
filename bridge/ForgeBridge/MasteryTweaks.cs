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
        public static IEnumerator Watch()
        {
            Type view = null, navBar = null;
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
