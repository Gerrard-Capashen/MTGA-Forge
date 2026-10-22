using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using Wizards.Mtga;
using Wotc.Mtga.Client.Models.Mercantile;
using Wotc.Mtga.Network.ServiceWrappers;
using Wotc.Mtga.Providers;

namespace ForgeBridge
{
    /// <summary>
    /// The profile's cosmetic pickers (avatars, sleeves, pets, emotes) list the entries of the STORE manager's catalogues, which
    /// the client only fills when the store is enabled. The store stays off offline, so this copies the catalogues the client builds
    /// itself from the available cosmetics (<c>IMercantileServiceWrapper.GetMercantileCollections</c>, answered by the Front Door with
    /// empty store listings) into the store manager's own collections. Runs for the whole session: a reloaded wrapper gets a new
    /// store manager.
    /// </summary>
    public static class CosmeticCatalogs
    {
        private static readonly FieldInfo Collections = typeof(StoreManager).GetField("MercantileCollections", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        public static IEnumerator Watch()
        {
            StoreManager done = null;
            while (true)
            {
                yield return new WaitForSeconds(0.25f);
                StoreManager store = null;
                try { store = WrapperController.Instance == null ? null : WrapperController.Instance.Store; } catch (Exception) { }
                if (store == null || store == done) continue;
                CosmeticsProvider cosmetics = null;
                try { cosmetics = Pantry.Get<CosmeticsProvider>(); } catch (Exception) { }
                // the catalogue arrives with StaticContent during start-up; wait for it
                if (cosmetics == null || cosmetics.AvailableAvatars.Count == 0) continue;
                if (Collections == null) { Log.Error("cosmetics: StoreManager.MercantileCollections not found"); yield break; }

                var promise = Pantry.Get<IMercantileServiceWrapper>().GetMercantileCollections();
                while (!promise.IsDone) yield return null;
                if (!promise.Successful)
                {
                    Log.Error("cosmetics: GetMercantileCollections failed: " + promise.Error);
                    done = store;       // do not retry in a loop
                    continue;
                }
                MercantileCollections mc = promise.Result;
                Collections.SetValue(store, mc);
                done = store;
                Log.Info("cosmetics: store catalogues filled: " + mc.AvatarCatalog.Count + " avatars, " + mc.CardBackCatalog.Count + " sleeves, "
                    + mc.PetCatalog.Count + " pets, " + mc.EmoteCatalog.Count + " emotes");
            }
        }
    }
}
