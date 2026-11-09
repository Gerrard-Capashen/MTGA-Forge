using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ForgeBridge
{
    /// <summary>
    /// Keeps the duel camera as Camera.main. The client unloads the AssetPrep scene only on its way to the home page; a duel
    /// reached without it (duel mode, rigs) kept AssetPrep's "Main Camera", which is tagged MainCamera like the duel camera and
    /// won Camera.main. Under URP Camera.current is null, so Dreamteck's SplineRenderer turns target / ability arrows towards
    /// Camera.main: they were drawn edge-on to the duel camera and only their 3D arrowheads showed.
    /// </summary>
    public static class DuelCamera
    {
        public static IEnumerator Watch()
        {
            bool inDuel = false;
            while (true)
            {
                yield return new WaitForSeconds(0.5f);
                bool duel = SceneManager.GetSceneByName("DuelScene").isLoaded;
                if (duel && !inDuel)
                {
                    var prep = SceneManager.GetSceneByName("AssetPrep");
                    if (prep.isLoaded)
                    {
                        Log.Info("duel camera: unloading the AssetPrep scene (its Main Camera would be Camera.main)");
                        yield return SceneManager.UnloadSceneAsync(prep);
                    }
                    var main = Camera.main;
                    Log.Info("duel camera: Camera.main is " + (main == null ? "none" : main.name + " (" + main.gameObject.scene.name + ")"));
                }
                inDuel = duel;
            }
        }
    }
}
