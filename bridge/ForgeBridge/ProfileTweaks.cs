using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace ForgeBridge
{
    /// <summary>
    /// Profile overview without the set collection entry: it opens a glitchy screen with a link to the Wizards store.
    /// The Patcher calls Apply at the end of ProfileDetailsPanel.Display (which fills the badge) and turns
    /// SetCollectionClicked into a no-op, so nothing else can reach the screen.
    /// </summary>
    public static class ProfileTweaks
    {
        public static void Apply(object panel)
        {
            try
            {
                var f = panel.GetType().GetField("_profileSetBadge", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var badge = f?.GetValue(panel) as Component;
                if (badge == null) return;
                // the badge sits inside the button that opens the collection: hide the whole entry
                // the whole tile (frame, "Set Collection" label, badge, button): CollectionPanel next to the Set Mastery tile; else the button around the badge
                for (var t = badge.transform; t != null; t = t.parent)
                {
                    if (t.name == "CollectionPanel") { t.gameObject.SetActive(false); return; }
                }
                var button = badge.GetComponentInParent<Button>(true);
                (button != null ? button.gameObject : badge.gameObject).SetActive(false);
            }
            catch (Exception e) { Log.Error("ProfileTweaks.Apply: " + e); }
        }
    }
}
