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
                if (needle == "arrows") { DumpArrows(at); continue; }
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

        /// <summary>"arrows": every intention arrow (target / ability / combat line) with its ends, spline points and renderers, to arrows_&lt;t&gt;.txt.</summary>
        private static void DumpArrows(float at)
        {
            string dir = Environment.GetEnvironmentVariable("FORGEBRIDGE_SHOT_DIR") ?? Application.persistentDataPath;
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var lines = new System.Collections.Generic.List<string>();
            lines.Add("camera " + (Camera.main == null ? "none" : Camera.main.name + " pos " + Camera.main.transform.position + " fwd " + Camera.main.transform.forward) + "  screen " + Screen.width + "x" + Screen.height
                + "  fixedDelta " + Time.fixedDeltaTime + " timeScale " + Time.timeScale);
            foreach (var cam in Camera.allCameras)
                lines.Add("cam " + FullPath(cam.transform) + " depth " + cam.depth + " ortho " + cam.orthographic + " mask " + cam.cullingMask + " defaultLayer " + ((cam.cullingMask & 1) != 0)
                    + " clear " + cam.clearFlags + " target " + (cam.targetTexture == null ? "screen" : cam.targetTexture.name) + " pos " + cam.transform.position + " fwd " + cam.transform.forward + " tag " + cam.tag + " scene " + cam.gameObject.scene.name
                    + " comps " + string.Join(",", cam.GetComponents<Component>().Select(x => x.GetType().Name)));
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var sc = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                lines.Add("scene " + sc.name + " loaded " + sc.isLoaded + " roots " + string.Join(",", sc.GetRootGameObjects().Select(g => g.name + (g.activeSelf ? "" : "(off)"))));
            }
            foreach (var mb in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (mb == null || mb.GetType().Name != "DreamteckIntentionArrowBehavior" || mb.gameObject.scene.name == null) continue;
                var tp = mb.GetType();
                string Call(string m) { try { return tp.GetMethod(m, F)?.Invoke(mb, null)?.ToString() ?? "null"; } catch (Exception e) { return "ERR " + e.InnerException?.Message; } }
                string Fld(string f) { try { var v = tp.GetField(f, F)?.GetValue(mb); return v is Component c ? (c == null ? "destroyed" : FullPath(c.transform)) : (v?.ToString() ?? "null"); } catch (Exception e) { return "ERR " + e.Message; } }
                lines.Add("ARROW " + FullPath(mb.transform) + " activeInHierarchy " + mb.gameObject.activeInHierarchy + " enabled " + mb.enabled + " pos " + mb.transform.position + " scale " + mb.transform.lossyScale);
                lines.Add("  start " + Call("GetStartPosition") + " from " + Fld("_startTransform") + " offset " + Fld("_startOffset") + "  end " + Call("GetEndPosition") + " to " + Fld("_endTransform"));
                lines.Add("  camera " + Fld("_camera") + " planeDist " + Fld("_cameraPlaneDistance") + " roundness " + Fld("_roundness") + " dirty " + Fld("_isDirty")
                    + " initial " + ((tp.GetField("_initialSpline", F)?.GetValue(mb) as Array)?.Length.ToString() ?? "null"));
                foreach (var c in mb.GetComponents<Component>())
                {
                    if (c == null) continue;
                    var ct = c.GetType();
                    if (ct.Name == "SplineComputer")
                    {
                        string pts = "?";
                        try
                        {
                            var gm = ct.GetMethods().First(m => m.Name == "GetPoints");
                            var args = gm.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : (p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)).ToArray();
                            var arr = gm.Invoke(c, args) as Array;
                            if (arr != null) pts = arr.Length + ": " + string.Join(" ", arr.Cast<object>().Select(p => p.GetType().GetField("position").GetValue(p) + "c" + p.GetType().GetField("color").GetValue(p) + "s" + p.GetType().GetField("size").GetValue(p)));
                        }
                        catch (Exception e) { pts = "ERR " + (e.InnerException ?? e).Message; }
                        lines.Add("  SplineComputer space " + ct.GetProperty("space")?.GetValue(c) + " type " + ct.GetProperty("type")?.GetValue(c) + " points " + pts);
                    }
                    else if (ct.Name == "SplineRenderer" || ct.BaseType?.Name == "MeshGenerator")
                    {
                        string P(string n) { try { return (ct.GetProperty(n, F)?.GetValue(c) ?? ct.GetField(n, F)?.GetValue(c))?.ToString() ?? "-"; } catch (Exception e) { return "ERR " + (e.InnerException ?? e).Message; } }
                        lines.Add("  " + ct.Name + " enabled " + ((Behaviour)c).enabled + " size " + P("size") + " clipFrom " + P("clipFrom") + " clipTo " + P("clipTo") + " slices " + P("slices") + " updateMethod " + P("updateMethod") + " autoUpdate " + P("autoUpdate"));
                    }
                }
                foreach (var r in mb.GetComponentsInChildren<Renderer>(true))
                {
                    var mf = r.GetComponent<MeshFilter>();
                    var mats = string.Join(",", r.sharedMaterials.Select(m => m == null ? "null" : m.name + "/" + (m.shader == null ? "noshader" : m.shader.name + (m.shader.isSupported ? "" : " UNSUPPORTED"))));
                    lines.Add("  renderer " + FullPath(r.transform) + " " + r.GetType().Name + " active " + r.gameObject.activeInHierarchy + " enabled " + r.enabled + " visible " + r.isVisible
                        + " layer " + LayerMask.LayerToName(r.gameObject.layer) + " bounds " + r.bounds.center + "/" + r.bounds.size
                        + " verts " + (mf == null ? "-" : mf.sharedMesh == null ? "nomesh" : mf.sharedMesh.vertexCount.ToString()) + " mats " + mats);
                    if (mf != null && mf.sharedMesh != null && mf.sharedMesh.isReadable && mf.sharedMesh.vertexCount < 1000)
                    {
                        var mesh = mf.sharedMesh;
                        var cols = mesh.colors;
                        var uvs = mesh.uv;
                        var vs = mesh.vertices;
                        lines.Add("    mesh tris " + mesh.triangles.Length / 3 + " colors " + cols.Length + (cols.Length > 0 ? " alpha " + cols.Min(x => x.a) + ".." + cols.Max(x => x.a) + " first " + cols[0] : "")
                            + " uv " + (uvs.Length > 0 ? uvs.Min(u => u.x) + ".." + uvs.Max(u => u.x) + " / " + uvs.Min(u => u.y) + ".." + uvs.Max(u => u.y) : "none")
                            + " v0 " + (vs.Length > 0 ? vs[0].ToString() : "-") + " v1 " + (vs.Length > 1 ? vs[1].ToString() : "-") + " vLast " + (vs.Length > 0 ? vs[vs.Length - 1].ToString() : "-"));
                    }
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null || m.shader == null) continue;
                        var props = new System.Collections.Generic.List<string>();
                        for (int i = 0; i < m.shader.GetPropertyCount(); i++)
                        {
                            string n = m.shader.GetPropertyName(i);
                            var t = m.shader.GetPropertyType(i);
                            string v = t == UnityEngine.Rendering.ShaderPropertyType.Color ? m.GetColor(n).ToString()
                                : t == UnityEngine.Rendering.ShaderPropertyType.Vector ? m.GetVector(n).ToString()
                                : t == UnityEngine.Rendering.ShaderPropertyType.Texture ? (m.GetTexture(n) == null ? "null" : m.GetTexture(n).name)
                                : m.GetFloat(n).ToString();
                            props.Add(n + "=" + v);
                        }
                        lines.Add("    mat " + m.name + " queue " + m.renderQueue + " keywords [" + string.Join(" ", m.shaderKeywords) + "] " + string.Join(" ", props));
                    }
                }
            }
            System.IO.File.WriteAllLines(System.IO.Path.Combine(dir, "arrows_" + (int)at + ".txt"), lines);
            Log.Info("dumped arrows: " + lines.Count(l => l.StartsWith("ARROW")));
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
