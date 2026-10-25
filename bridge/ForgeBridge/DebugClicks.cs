using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ForgeBridge
{
    /// <summary>
    /// Dev aid: FORGEBRIDGE_CLICKS="45:keep,60:pass" clicks active GameObjects whose name contains the text, at the given seconds.
    /// Menu helpers: "dump" writes every active clickable object (path + visible text) to dump_&lt;t&gt;.txt in FORGEBRIDGE_SHOT_DIR;
    /// "text=Play" clicks the clickable whose own/child label equals the text (case-insensitive); a suffix "@n" clicks only
    /// the nth candidate (e.g. "deckview@0").
    /// </summary>
    public static class DebugClicks
    {
        public static IEnumerator Run()
        {
            string spec = Environment.GetEnvironmentVariable("FORGEBRIDGE_CLICKS");
            if (string.IsNullOrEmpty(spec)) yield break;
            foreach (var part in spec.Split(','))
            {
                var kv = part.Split(':');
                // invariant culture: on a Spanish/German system "61.5" would not parse and the click was skipped
                if (kv.Length != 2 || !float.TryParse(kv[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float at)) continue;
                while (Time.realtimeSinceStartup < at) yield return null;
                string needle = kv[1].ToLowerInvariant();
                if (needle == "dump") { Dump(at); continue; }
                bool hover = needle.StartsWith("hover~");    // "hover~panel_free@3": pointer enter only (reward tooltips), no click
                if (hover) needle = needle.Substring(6);
                bool? setTo = null;   // "name=on" / "name=off": set a Toggle instead of flipping it
                if (needle.EndsWith("=on")) { setTo = true; needle = needle.Substring(0, needle.Length - 3); }
                else if (needle.EndsWith("=off")) { setTo = false; needle = needle.Substring(0, needle.Length - 4); }
                int pick = -1;
                int atSign = needle.LastIndexOf('@');
                if (atSign > 0 && int.TryParse(needle.Substring(atSign + 1), out int pn)) { pick = pn; needle = needle.Substring(0, atSign); }
                string clickType = Environment.GetEnvironmentVariable("FORGEBRIDGE_CLICKTYPE") ?? "Primary";
                int bar = needle.IndexOf('|');
                if (bar >= 0) { string t = kv[1].Substring(bar + 1); clickType = t.Equals("dbl", StringComparison.OrdinalIgnoreCase) ? "DoublePrimary" : "Primary"; needle = needle.Substring(0, bar); }
                var hits = Resources.FindObjectsOfTypeAll<Transform>()
                    .Where(t => t.gameObject.activeInHierarchy && (needle.StartsWith("cdc #") ? t.name.ToLowerInvariant() == needle : t.name.ToLowerInvariant().Contains(needle))).ToList();
                // "hand0".."hand9": the Nth card (by instance id) of the local hand
                if (needle.StartsWith("hand") && needle.Length == 5 && char.IsDigit(needle[4]))
                {
                    int n = needle[4] - '0';
                    hits = Resources.FindObjectsOfTypeAll<Transform>()
                        .Where(t => t.gameObject.activeInHierarchy && t.name.StartsWith("CDC #") && t.parent != null && t.parent.name.Contains("LocalHand"))
                        .OrderBy(t => t.GetSiblingIndex()).Skip(n).Take(1).ToList();
                }
                // "bf0".."bf9": the Nth battlefield card ordered by instance id (numeric; played cards get ids >= 1000)
                if (needle.StartsWith("bf") && needle.Length == 3 && char.IsDigit(needle[2]))
                {
                    int n = needle[2] - '0';
                    hits = Resources.FindObjectsOfTypeAll<Transform>()
                        .Where(t => t.gameObject.activeInHierarchy && t.name.StartsWith("CDC #") && t.parent != null && t.parent.name.Contains("BattlefieldCardHolder"))
                        .OrderBy(t => { int.TryParse(t.name.Substring(5).Split(' ')[0], out int id); return id; }).Skip(n).Take(1).ToList();
                }
                if (needle.Contains("/"))   // path fragment, e.g. "blade_findmatch/buttons/mainbutton"
                {
                    hits = Resources.FindObjectsOfTypeAll<Transform>()
                        .Where(t => t.gameObject.activeInHierarchy && FullPath(t).ToLowerInvariant().EndsWith(needle)).ToList();
                }
                if (needle.StartsWith("text~"))   // label contains; deck tiles etc. are clicked through their Hitbox child
                {
                    string want = needle.Substring(5).Trim();
                    var all = Clickables().Where(t => Label(t).ToLowerInvariant().Contains(want)).ToList();
                    var withHitbox = all.Where(t => t.Find("Hitbox") != null).Select(t => t.Find("Hitbox")).ToList();
                    hits = withHitbox.Count > 0 ? withHitbox : all;
                }
                if (needle.StartsWith("text="))
                {
                    string want = needle.Substring(5).Trim();
                    hits = Clickables().Where(t => Label(t).Trim().ToLowerInvariant() == want).ToList();
                }
                if (pick >= 0) hits = hits.Skip(pick).Take(1).ToList();
                Log.Info("click '" + needle + "': " + hits.Count + " candidates: " + string.Join(" | ", hits.Take(12).Select(h => Path(h))));
                foreach (var h in hits)
                {
                    if (hover)
                    {
                        ExecuteEvents.Execute(h.gameObject, new PointerEventData(EventSystem.current), ExecuteEvents.pointerEnterHandler);
                        Log.Info("pointer enter on " + h.name);
                        continue;
                    }
                    var ci = h.GetComponent<CardInput>();
                    if (ci != null)
                    {
                        var mi = typeof(CardInput).GetMethod("ProcessInteraction");
                        mi.Invoke(ci, new object[] { Enum.Parse(mi.GetParameters()[0].ParameterType, clickType) });
                        Log.Info("CardInput.ProcessInteraction(Primary) on " + h.name);
                        continue;
                    }
                    var chb = h.GetComponentInChildren<ClickAndHoldButton>();
                    if (chb != null)
                    {
                        var cev = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left, clickCount = 1 };
                        ExecuteEvents.Execute(chb.gameObject, cev, ExecuteEvents.pointerDownHandler);
                        ExecuteEvents.Execute(chb.gameObject, cev, ExecuteEvents.pointerUpHandler);
                        ExecuteEvents.Execute(chb.gameObject, cev, ExecuteEvents.pointerClickHandler);
                        Log.Info("ClickAndHoldButton clicked under " + h.name);
                        continue;
                    }
                    var tog = h.GetComponent<UnityEngine.UI.Toggle>();
                    if (tog != null)
                    {
                        tog.isOn = setTo ?? !tog.isOn;
                        Log.Info("Toggle " + h.name + " -> " + tog.isOn);
                        continue;
                    }
                    var btn = h.GetComponent<UnityEngine.UI.Button>();
                    if (btn != null)
                    {
                        btn.onClick.Invoke();
                        Log.Info("Button.onClick on " + h.name);
                        continue;
                    }
                    var cb = h.GetComponent<CustomButton>();
                    if (cb != null)
                    {
                        cb.Click();     // menu buttons: OnPointerUp only clicks while the pointer is "over" them
                        Log.Info("CustomButton.Click on " + h.name);
                        continue;
                    }
                    var ev = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left, clickCount = 1 };
                    ExecuteEvents.Execute(h.gameObject, ev, ExecuteEvents.pointerEnterHandler);
                    ExecuteEvents.Execute(h.gameObject, ev, ExecuteEvents.pointerDownHandler);
                    ExecuteEvents.Execute(h.gameObject, ev, ExecuteEvents.pointerUpHandler);
                    ExecuteEvents.Execute(h.gameObject, ev, ExecuteEvents.pointerClickHandler);
                    ExecuteEvents.Execute(h.gameObject, ev, ExecuteEvents.pointerExitHandler);
                }
            }
        }

        private static System.Collections.Generic.IEnumerable<Transform> Clickables()
        {
            return Resources.FindObjectsOfTypeAll<Transform>().Where(t => t.gameObject.activeInHierarchy
                && t.GetComponents<MonoBehaviour>().Any(m => m is IPointerClickHandler || m is IPointerDownHandler || m is UnityEngine.UI.Selectable));
        }

        /// <summary>Visible text on the object or its children (TextMeshPro / legacy Text).</summary>
        private static string Label(Transform t)
        {
            var parts = t.GetComponentsInChildren<TMPro.TMP_Text>(false).Select(x => x.text)
                .Concat(t.GetComponentsInChildren<UnityEngine.UI.Text>(false).Select(x => x.text))
                .Where(x => !string.IsNullOrWhiteSpace(x)).Take(4);
            return string.Join(" / ", parts).Replace('\n', ' ');
        }

        private static void Dump(float at)
        {
            string dir = Environment.GetEnvironmentVariable("FORGEBRIDGE_SHOT_DIR") ?? Application.persistentDataPath;
            var lines = Clickables().Select(t => FullPath(t) + "  [" + Label(t) + "]").OrderBy(x => x).ToList();
            System.IO.File.WriteAllLines(System.IO.Path.Combine(dir, "dump_" + (int)at + ".txt"), lines);
            Log.Info("dumped " + lines.Count + " clickables");
        }

        private static string FullPath(Transform t)
        {
            string s = t.name;
            while (t.parent != null) { t = t.parent; s = t.name + "/" + s; }
            return s;
        }

        private static string Path(Transform t)
        {
            string s = t.name;
            for (int i = 0; i < 3 && t.parent != null; i++) { t = t.parent; s = t.name + "/" + s; }
            return s;
        }
    }
}
