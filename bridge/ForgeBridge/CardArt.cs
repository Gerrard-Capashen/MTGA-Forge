using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;

namespace ForgeBridge
{
    /// <summary>
    /// Art for the cards CardDbBuilder added (art id 9,000,000+, no art bundle). The Patcher hooks AssetLoader so their
    /// art paths ("Assets/Core/CardArt/900000/9000123_AIF.tga") resolve here: one texture per art id, showing a spinner
    /// right away and filled in place (Texture2D.LoadImage on the same object) when the local server's art endpoint
    /// answers, so every card already showing it updates. Only cards actually shown are fetched, once per session;
    /// the server caches them on disk. The client talks to loopback only (forge.mtga.art.ArtServer fetches Scryfall).
    /// </summary>
    public static class CardArt
    {
        private const int MinArtId = 9_000_000;
        private const int Width = 128, Height = 96;
        private static readonly Regex ArtPath = new Regex(@"^Assets/Core/CardArt/\d{6}/(\d{7,9})_AIF\.(tga|jpg|png)$", RegexOptions.Compiled);
        public static readonly int Port = int.TryParse(Environment.GetEnvironmentVariable("FORGEBRIDGE_ARTPORT"), out var p) ? p : 30011;

        private static readonly Dictionary<int, Texture2D> textures = new Dictionary<int, Texture2D>();
        private static readonly HashSet<int> pending = new HashSet<int>();
        private static readonly ConcurrentQueue<KeyValuePair<int, byte[]>> arrived = new ConcurrentQueue<KeyValuePair<int, byte[]>>();
        private static readonly HttpClient http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(90) };
        private static Color32[] frame;

        private static int ArtId(string path)
        {
            if (path == null || path.Length < 30 || !path.StartsWith("Assets/Core/CardArt/", StringComparison.Ordinal)) return 0;
            var m = ArtPath.Match(path);
            return m.Success && int.TryParse(m.Groups[1].Value, out int id) && id >= MinArtId ? id : 0;
        }

        private static readonly Dictionary<string, Wotc.Mtga.Cards.ArtCrops.ArtCrop> crops = new Dictionary<string, Wotc.Mtga.Cards.ArtCrops.ArtCrop>();
        /// <summary>A crop lookup names the art without an extension ("Assets/Core/CardArt/901000/9011370_AIF").</summary>
        private static readonly Regex CropPath = new Regex(@"^Assets/Core/CardArt/\d{6}/(\d{7,9})_AIF(\.(tga|jpg|png))?$", RegexOptions.Compiled);
                private static readonly HashSet<string> loggedFormats = new HashSet<string>();
        private const float SourceRatio = 626f / 457f;      // Scryfall's art crops

        /// <summary>
        /// Hook (SqlArtCropProvider.GetCrop): our art (Scryfall crops, 1.37:1, no crop rows in the client's database) is stretched over a frame of any other
        /// shape (an emblem's hover card is taller than the board's): a centred crop to the format's aspect keeps its proportions. Null = the client's own answer.
        /// </summary>
        public static Wotc.Mtga.Cards.ArtCrops.ArtCrop Crop(object provider, string artPath, string format)
        {
            try
            {
                if (artPath == null || !CropPath.IsMatch(artPath) || !int.TryParse(CropPath.Match(artPath).Groups[1].Value, out int cropArt) || cropArt < MinArtId) return null;
                lock (crops)
                {
                    if (loggedFormats.Add(format ?? "<null>")) Log.Info("art crop requested: format '" + (format ?? "<null>") + "' ratio " + ((provider as Wotc.Mtga.Cards.ArtCrops.IArtCropProvider)?.GetFormat(format)?.Ratio.ToString() ?? "unknown") + " path " + artPath);
                    if (format == null) return null;
                    if (crops.TryGetValue(format, out var cached)) return cached;
                    var f = (provider as Wotc.Mtga.Cards.ArtCrops.IArtCropProvider)?.GetFormat(format);
                    Wotc.Mtga.Cards.ArtCrops.ArtCrop crop = null;
                    if (f != null && f.Ratio > 0.05f && Math.Abs(f.Ratio - SourceRatio) / SourceRatio > 0.02f)
                    {
                        float r = f.Ratio;
                        crop = new Wotc.Mtga.Cards.ArtCrops.ArtCrop();
                        if (r < SourceRatio) { float xs = r / SourceRatio; crop.ScaleOffset = new Vector4(xs, 1f, (1f - xs) / 2f, 0f); }
                        else { float ys = SourceRatio / r; crop.ScaleOffset = new Vector4(1f, ys, 0f, (1f - ys) / 2f); }
                    }
                    crops[format] = crop;
                    return crop;
                }
            }
            catch (Exception e) { Log.Error("CardArt.Crop: " + e); return null; }
        }

        /// <summary>Hook (AssetLoader.HaveAsset/ReleaseAsset/AddReferenceCount): the path is one of ours, not a bundle asset.</summary>
        public static bool Owns(string path) => ArtId(path) != 0 || EventSet(path) != null || CosmeticIcons.Owns(path);

        /// <summary>Hook (AssetLoader.AcquireAsset&lt;T&gt;): the texture for one of our paths, null for any other path.</summary>
        public static UnityEngine.Object Acquire(string path)
        {
            if (CosmeticIcons.Owns(path)) return CosmeticIcons.Acquire(path);
            string set = EventSet(path);
            if (set != null) return EventSprite(set);
            int id = ArtId(path);
            if (id == 0) return null;
            if (textures.TryGetValue(id, out var tex) && tex != null) return tex;
            tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
            {
                name = "ForgeArt_" + id,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            textures[id] = tex;
            pending.Add(id);
            Draw(tex, Time.realtimeSinceStartup);
            Task.Run(() => Fetch(id));
            return tex;
        }

        // ---- quick draft event art: "forge-event-art/<SET>.jpg" is a 16:9 crop of a showcase card of the set ----

        private const int EventWidth = 640, EventHeight = 360, BackgroundWidth = 1920, BackgroundHeight = 1080;
        private static readonly Regex EventPath = new Regex(@"^forge-event-art/([A-Za-z0-9]{2,8}(-p)?(-bg)?)\.jpg$", RegexOptions.Compiled);
        private static readonly Dictionary<string, Sprite> eventSprites = new Dictionary<string, Sprite>();
        /// <summary>Per event art key ("ZEN-bg"): the client's own art path for the event, shown when ours cannot be had.</summary>
        private static readonly Dictionary<string, string> eventDefaults = new Dictionary<string, string>();
        private static readonly HashSet<string> eventFailed = new HashSet<string>();
        private static readonly AssetLoader.AssetTracker<Sprite> defaultTracker = new AssetLoader.AssetTracker<Sprite>("ForgeEventArtDefault");
        private static readonly ConcurrentQueue<KeyValuePair<string, byte[]>> arrivedEvent = new ConcurrentQueue<KeyValuePair<string, byte[]>>();

        private static string EventSet(string path)
        {
            if (path == null || !path.StartsWith("forge-event-art/", StringComparison.Ordinal)) return null;
            var m = EventPath.Match(path);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>Hook for the event-art fallback (Core patch): the path to use when the client has no art for this event.</summary>
        public static string EventArtPath(string current, Wizards.MDN.EventContext ctx) => EventArt(current, ctx, "", false);

        /// <summary>Same for the event page's full-size background (a soft composite, see the art server).</summary>
        public static string EventBackgroundPath(string current, Wizards.MDN.EventContext ctx) => EventArt(current, ctx, "-bg", true);

        /// <param name="replace">true: also replace art the client found (the background lookup answers a default image for unknown events).</param>
        private static string EventArt(string current, Wizards.MDN.EventContext ctx, string suffix, bool replace)
        {
            if (!replace && !string.IsNullOrEmpty(current)) return current;
            string name = null;
            try { name = ctx?.PlayerEvent?.EventUXInfo?.PublicEventName; } catch (Exception) { }
            string prefix = name == null ? null : name.StartsWith("QuickDraft_", StringComparison.Ordinal) ? "QuickDraft_"
                : name.StartsWith("PremierDraft_", StringComparison.Ordinal) ? "PremierDraft_" : null;
            if (name == "Archenemy_Sparky") return ArchenemyKey(current, suffix);
            if (prefix == null) return current;
            // a premier draft has its own tile ("<SET>-p"), so it differs from the same set's quick draft
            string key = name.Substring(prefix.Length) + (prefix == "PremierDraft_" ? "-p" : "") + suffix;
            if (eventFailed.Contains(key)) return current;      // no art for it this session: the client's own
            if (!string.IsNullOrEmpty(current)) eventDefaults[key] = current;
            return "forge-event-art/" + key + ".jpg";
        }

        /// <summary>Archenemy: Sparky has its own art (the art server's "SPARKY" key: an MH3 illustration for the tile, a FRA one for the page).</summary>
        private static string ArchenemyKey(string current, string suffix)
        {
            string key = "SPARKY" + suffix;
            if (eventFailed.Contains(key)) return current;
            if (!string.IsNullOrEmpty(current)) eventDefaults[key] = current;
            return "forge-event-art/" + key + ".jpg";
        }

        private static Sprite EventSprite(string set)
        {
            if (eventSprites.TryGetValue(set, out var sprite) && sprite != null) return sprite;
            bool bg = set.EndsWith("-bg", StringComparison.Ordinal);
            int w = bg ? BackgroundWidth : EventWidth, h = bg ? BackgroundHeight : EventHeight;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                name = "ForgeEventArt_" + set,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            Fill(tex, new Color32(32, 32, 38, 255));
            sprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
            sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            eventSprites[set] = sprite;
            Task.Run(() => FetchEvent(set));
            return sprite;
        }

        /// <summary>Copy the client's own sprite for the event into our placeholder texture (so the page shows its default, not a gray frame).</summary>
        private static void ShowDefault(Texture2D target, string path)
        {
            try
            {
                var sprite = defaultTracker.Acquire(path);
                if (sprite == null || sprite.texture == null) return;
                var rt = RenderTexture.GetTemporary(target.width, target.height, 0);
                var prev = RenderTexture.active;
                Graphics.Blit(sprite.texture, rt);
                RenderTexture.active = rt;
                target.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                target.Apply(false);
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
            catch (Exception e)
            {
                Log.Error("EventArt default: " + e.Message);
            }
        }

        private static async Task FetchEvent(string set)
        {
            byte[] bytes = null;
            for (int attempt = 0; attempt < 3 && bytes == null; attempt++)
            {
                try
                {
                    using (var resp = await http.GetAsync("http://127.0.0.1:" + Port + "/event/" + set))
                    {
                        if (resp.StatusCode == HttpStatusCode.OK) bytes = await resp.Content.ReadAsByteArrayAsync();
                        else break;
                    }
                }
                catch (Exception e)
                {
                    if (attempt == 2) Log.Error("EventArt " + set + ": " + e.Message);
                    await Task.Delay(2000);
                }
            }
            arrivedEvent.Enqueue(new KeyValuePair<string, byte[]>(set, bytes));
        }

        private static async Task Fetch(int id)
        {
            byte[] bytes = null;
            for (int attempt = 0; attempt < 3 && bytes == null; attempt++)
            {
                try
                {
                    using (var resp = await http.GetAsync("http://127.0.0.1:" + Port + "/art/" + id))
                    {
                        if (resp.StatusCode == HttpStatusCode.OK) bytes = await resp.Content.ReadAsByteArrayAsync();
                        else break; // the server's final answer (no art for this card)
                    }
                }
                catch (Exception e)
                {
                    // server not up (yet): try again shortly
                    if (attempt == 2) Log.Error("CardArt " + id + ": " + e.Message);
                    await Task.Delay(2000);
                }
            }
            arrived.Enqueue(new KeyValuePair<int, byte[]>(id, bytes));
        }

        /// <summary>Main-thread loop (started by LocalStartup): applies arrived art and animates the spinners.</summary>
        public static IEnumerator Pump()
        {
            float nextFrame = 0f;
            while (true)
            {
                while (arrived.TryDequeue(out var a))
                {
                    pending.Remove(a.Key);
                    if (!textures.TryGetValue(a.Key, out var tex) || tex == null) continue;
                    if (a.Value == null || !tex.LoadImage(a.Value))
                    {
                        Fill(tex, new Color32(40, 40, 46, 255));
                        Log.Info("CardArt " + a.Key + ": no art");
                    }
                }
                while (arrivedEvent.TryDequeue(out var e))
                {
                    if (e.Value != null && eventSprites.TryGetValue(e.Key, out var sprite) && sprite != null && sprite.texture.LoadImage(e.Value)) continue;
                    Log.Info("EventArt " + e.Key + ": no art");
                    eventFailed.Add(e.Key);
                    if (eventSprites.TryGetValue(e.Key, out var ours) && ours != null && eventDefaults.TryGetValue(e.Key, out var path)) ShowDefault(ours.texture, path);
                }
                float now = Time.realtimeSinceStartup;
                if (pending.Count > 0 && now >= nextFrame)
                {
                    nextFrame = now + 0.08f;
                    foreach (int id in pending)
                    {
                        if (textures.TryGetValue(id, out var tex) && tex != null) Draw(tex, now);
                    }
                }
                yield return null;
            }
        }

        private static void Fill(Texture2D tex, Color32 c)
        {
            var px = new Color32[tex.width * tex.height];
            for (int i = 0; i < px.Length; i++) px[i] = c;
            tex.SetPixels32(px);
            tex.Apply(false);
        }

        /// <summary>Dark backdrop with a ring of 10 dots; the bright dot goes round about once a second.</summary>
        private static void Draw(Texture2D tex, float time)
        {
            if (frame == null) frame = new Color32[Width * Height];
            var bg = new Color32(32, 32, 38, 255);
            for (int i = 0; i < frame.Length; i++) frame[i] = bg;
            const int dots = 10, ring = 22, dot = 4;
            int head = (int)(time * dots) % dots;
            for (int d = 0; d < dots; d++)
            {
                double ang = 2 * Math.PI * d / dots;
                int cx = Width / 2 + (int)Math.Round(ring * Math.Sin(ang)), cy = Height / 2 + (int)Math.Round(ring * Math.Cos(ang));
                float fade = 1f - ((head - d + dots) % dots) / (float)dots;
                byte v = (byte)(60 + 195 * fade * fade);
                var c = new Color32(v, v, v, 255);
                for (int y = -dot; y <= dot; y++)
                for (int x = -dot; x <= dot; x++)
                    if (x * x + y * y <= dot * dot) frame[(cy + y) * Width + cx + x] = c;
            }
            tex.SetPixels32(frame);
            tex.Apply(false);
        }
    }
}
