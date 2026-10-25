using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace ForgeBridge
{
    /// <summary>
    /// Mastery track hover popups (server MasteryTrack): the client's popup objects render a given sleeve, but its avatar object needs
    /// full-body art most avatars lack, and nothing renders a title, pet or sticker (an empty circle). Those rewards use the avatar object with
    /// referenceId "ForgeIcon:&lt;image&gt;"; while a NotificationPopup shows one, its image gets the avatar's own portrait
    /// ("ForgeIcon:ForgeAvatar_&lt;id&gt;") or the generic icon of the kind ("ForgeIcon:&lt;server reward image&gt;"). The popup keeps its objects in a static table keyed by
    /// prefab path + "." + referenceId: that is how the hovered reward is known.
    /// </summary>
    public static class RewardPopupIcons
    {
        private const string Marker = "ForgeIcon:";
        private const string ServerRewards = "Assets/Core/Meta/NetworkResources/ServerRewards/";
        private static readonly AssetLoader.AssetTracker<Sprite> tracker = new AssetLoader.AssetTracker<Sprite>("ForgeRewardPopupIcons");
        private static readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>();

        public static IEnumerator Watch()
        {
            Type popupType = null;
            FieldInfo instanceField = null, storedField = null;
            while (true)
            {
                yield return new WaitForSeconds(0.1f);
                try
                {
                    if (popupType == null)
                    {
                        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            popupType = asm.GetType("NotificationPopup", false);
                            if (popupType != null) break;
                        }
                        if (popupType == null) continue;
                        const BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
                        instanceField = popupType.GetField("_rewardInstance", f);
                        storedField = popupType.GetField("_storedPrefabsByPath", BindingFlags.Static | BindingFlags.NonPublic);
                        if (instanceField == null || storedField == null) { Log.Error("RewardPopupIcons: NotificationPopup fields not found"); yield break; }
                    }
                    var stored = storedField.GetValue(null) as IDictionary;
                    foreach (var o in UnityEngine.Object.FindObjectsOfType(popupType))
                    {
                        if (!(o is Component popup) || popup == null) continue;
                        var instance = instanceField.GetValue(popup) as Component;
                        string icon = null;
                        if (instance != null && instance.gameObject.activeSelf && stored != null)
                        {
                            foreach (DictionaryEntry e in stored)
                            {
                                if (!ReferenceEquals(e.Value, instance)) continue;
                                icon = IconFor((string)e.Key);
                                break;
                            }
                        }
                        Show(popup, instance, icon);
                    }
                }
                catch (Exception e)
                {
                    Log.Error("RewardPopupIcons: " + e);
                    yield break;
                }
            }
        }

        /// <summary>The image to lay into the popup for a stored popup-object key, null for the client's own objects.</summary>
        private static string IconFor(string key)
        {
            int m = key.IndexOf(Marker, StringComparison.Ordinal);
            if (m >= 0) return key.Substring(m + Marker.Length);
            if (key.Contains("RewardPopup3DIcon_MetaGoalAndPhrase.")) return "ObjectiveIcon_Phrase_Generic";
            return null;
        }

        /// <summary>Original look of each popup's reward circle, put back when the popup shows a reward the client draws itself (a sleeve).</summary>
        private static readonly Dictionary<Image, KeyValuePair<Sprite, bool>> circles = new Dictionary<Image, KeyValuePair<Sprite, bool>>();

        /// <summary>
        /// The popup's reward circle (RewardCircle_CONTAINER, the parent of the popup object) shows the reward's picture: the avatar's portrait or
        /// the generic icon of the kind. The popup objects themselves draw nothing for these rewards (the avatar object wants full-body art
        /// that most avatars lack; an image put into it did not show).
        /// </summary>
        private static void Show(Component popup, Component instance, string icon)
        {
            Transform container = instance != null ? instance.transform.parent : null;
            var circle = container != null ? container.GetComponent<Image>() : null;
            if (circle == null) return;
            if (!circles.TryGetValue(circle, out var original)) circles[circle] = original = new KeyValuePair<Sprite, bool>(circle.sprite, circle.preserveAspect);
            if (icon == null)
            {
                if (circle.sprite != original.Key) { circle.sprite = original.Key; circle.preserveAspect = original.Value; }
                return;
            }
            var sprite = SpriteFor(icon);
            if (sprite == null || circle.sprite == sprite) return;
            circle.sprite = sprite;
            circle.preserveAspect = true;
        }

        private static Sprite SpriteFor(string icon)
        {
            if (sprites.TryGetValue(icon, out var s) && s != null) return s;
            s = icon.StartsWith("ForgeAvatar_")
                ? CosmeticIcons.Acquire(ServerRewards + icon + ".png") as Sprite
                : tracker.Acquire(ServerRewards + icon + ".png");
            sprites[icon] = s;
            return s;
        }
    }
}
