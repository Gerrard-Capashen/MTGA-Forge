using System;
using System.Collections.Generic;
using Core.Code.AssetLookupTree.AssetLookup;
using UnityEngine;
using Wizards.Mtga;

namespace ForgeBridge
{
    /// <summary>
    /// Mastery track icons of the actual cosmetic (server MasteryTrack): a reward image "ForgeAvatar_&lt;avatar id&gt;" (the client loads it as
    /// Assets/Core/Meta/NetworkResources/ServerRewards/ForgeAvatar_&lt;id&gt;.png) is answered with the avatar's own thumbnail sprite, found
    /// through the client's asset lookup (ProfileUtilities.GetAvatarThumbImagePath); the generic avatar icon when there is none.
    /// Sleeves and pets are 3D in the client (no sprite to show): their icon stays generic and the hover popup shows the real thing.
    /// </summary>
    public static class CosmeticIcons
    {
        private const string Prefix = "/ServerRewards/ForgeAvatar_";
        private const string Generic = "Assets/Core/Meta/NetworkResources/ServerRewards/ObjectiveIcon_Avatar_Generic.png";
        private static readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>();
        private static readonly AssetLoader.AssetTracker<Sprite> tracker = new AssetLoader.AssetTracker<Sprite>("ForgeCosmeticIcons");

        public static bool Owns(string path) => path != null && path.IndexOf(Prefix, StringComparison.Ordinal) >= 0;

        public static UnityEngine.Object Acquire(string path)
        {
            int i = path.IndexOf(Prefix, StringComparison.Ordinal);
            string id = path.Substring(i + Prefix.Length);
            if (id.EndsWith(".png")) id = id.Substring(0, id.Length - 4);
            if (sprites.TryGetValue(id, out var cached) && cached != null) return cached;
            Sprite sprite = null;
            try
            {
                var als = Pantry.Get<AssetLookupManager>().AssetLookupSystem;
                string thumb = ProfileUtilities.GetAvatarThumbImagePath(als, id);
                if (!string.IsNullOrEmpty(thumb)) sprite = tracker.Acquire(thumb);
            }
            catch (Exception e) { Log.Error("CosmeticIcons " + id + ": " + e.Message); }
            if (sprite == null) sprite = tracker.Acquire(Generic);
            sprites[id] = sprite;
            return sprite;
        }
    }
}
