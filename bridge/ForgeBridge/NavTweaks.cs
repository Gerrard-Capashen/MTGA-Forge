using System;
using System.Reflection;
using UnityEngine;

namespace ForgeBridge
{
    /// <summary>
    /// Top bar without what has nothing behind it offline: the Packs, Store and Mastery tabs (shown locked otherwise) and the
    /// wildcard, gold and gem counters (always 0). The Patcher calls Apply when NavBarController.UpdateButtons returns; its
    /// tab and currency visibility is set before its first await, so Apply runs after it on every refresh.
    /// </summary>
    public static class NavTweaks
    {
        private static readonly string[] Hidden = { "PacksButton", "StoreButton", "MasteryButton", "coinButton", "gemButton", "_wildcardButton" };

        public static void Apply(object navBar)
        {
            try
            {
                foreach (var name in Hidden)
                {
                    var f = navBar.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (f?.GetValue(navBar) is Component c && c != null) c.gameObject.SetActive(false);
                }
            }
            catch (Exception e) { Log.Error("NavTweaks.Apply: " + e); }
        }
    }
}
