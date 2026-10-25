using System;
using System.Collections;
using UnityEngine;

namespace ForgeBridge
{
    /// <summary>
    /// The mastery track page (server MasteryTrack) borrows a set track's prefab, which has a "Mastery Tree" button (the orb web).
    /// The Forge track has no web: while a RewardTrackView is open, the button is switched off.
    /// </summary>
    public static class MasteryTweaks
    {
        public static IEnumerator Watch()
        {
            Type view = null;
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
