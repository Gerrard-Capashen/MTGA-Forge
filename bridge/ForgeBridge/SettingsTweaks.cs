using System;
using System.Collections;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ForgeBridge
{
    /// <summary>
    /// Options menu for a client with no Wizards account: no log out, no privacy entries (links to Wizards' site), and the
    /// Account panel loses what needs Wizards services. Report a bug stays; its support link is overridden in FormatLoc.
    /// The Patcher calls Apply at the end of SettingsMenu.Open, after the menu decided which buttons to show.
    /// </summary>
    public static class SettingsTweaks
    {
        private static bool _logged;

        private static object Field(object o, string name)
        {
            var f = o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return f?.GetValue(o);
        }

        public static void Apply(SettingsMenu menu)
        {
            try
            {
                (Field(menu, "LogoutButton") as GameObject)?.SetActive(false);

                // sub-menu entries: the panel type tells which button is which
                if (Field(menu, "_settingsPanels") is IEnumerable panels)
                    foreach (var entry in panels)
                    {
                        var panel = Field(entry, "Panel") as Component;
                        var button = Field(entry, "Button") as Component;
                        if (panel == null || button == null) continue;
                        if (panel.GetType().Name == "SettingsPanelPrivacyPolicy") button.gameObject.SetActive(false);
                        else if (panel.GetType().Name == "SettingsPanelAccount") TrimAccount(panel);
                        else if (panel.GetType().Name == "SettingsPanelReportIssue") TrimReportIssue(panel);
                    }

                // "Your privacy choices" is not a panel of its own: find main menu entries by their label
                if (Field(menu, "MainMenuPanel") is GameObject main)
                    foreach (var label in main.GetComponentsInChildren<TMP_Text>(true))
                    {
                        if (!_logged) Log.Info("settings menu entry: '" + label.text + "' (" + label.transform.parent?.name + ")");
                        if (label.text == null || label.text.IndexOf("privacy", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var target = label.GetComponentInParent<Button>(true)?.gameObject ?? label.transform.parent?.gameObject;
                        target?.SetActive(false);
                    }
                _logged = true;
            }
            catch (Exception e) { Log.Error("SettingsTweaks.Apply: " + e); }
        }

        private static bool IsButton(Component c) => c is Button || c.GetType().Name == "CustomButton";

        private static void Dump(Component panel)
        {
            foreach (var t in panel.GetComponentsInChildren<Transform>(true))
            {
                var kinds = string.Join(",", Array.ConvertAll(t.GetComponents<Component>(), c => c == null ? "?" : c.GetType().Name));
                var text = t.GetComponent<TMP_Text>();
                Log.Info("  " + panel.GetType().Name + "/" + t.name + " [" + kinds + "]" + (text != null ? " '" + text.text + "'" : ""));
            }
        }

        private static void TrimAccount(Component panel)
        {
            if (!_logged) Dump(panel);
            // Everything that opens Wizards' site (account preferences, delete account, terms, code of conduct, promotions, drop rates)
            // and the store restore are buttons; the back button and the social toggles stay.
            var back = Field(panel, "_backButton") as Component;
            foreach (var c in panel.GetComponentsInChildren<Component>(true))
                if (c != null && c != back && IsButton(c) && !c.transform.IsChildOf(back.transform)) c.gameObject.SetActive(false);
            // Discord chat account: nothing behind it here (ShowPanel would re-enable it, a destroyed one is skipped)
            var discord = Field(panel, "_discordUnlinkButton") as Component;
            if (discord != null) UnityEngine.Object.Destroy(discord.gameObject);
            // detailed logs: the patched client logs on its own
            if (Field(panel, "_trackerToggle") is Toggle tracker)
            {
                var row = tracker.transform.parent;
                bool alone = row != null && row != panel.transform && row.GetComponentsInChildren<Toggle>(true).Length == 1;
                (alone ? row.gameObject : tracker.gameObject).SetActive(false);
            }
        }

        private static void TrimReportIssue(Component panel)
        {
            if (!_logged) Dump(panel);
            // "Repair game files" restarts the client and re-hashes every file
            foreach (var b in panel.GetComponentsInChildren<Core.Meta.MainNavigation.SettingsMenu.ValidateBundlesButton>(true))
                b.gameObject.SetActive(false);
        }
    }
}
