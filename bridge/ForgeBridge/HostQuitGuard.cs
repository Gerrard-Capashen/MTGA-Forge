using System;
using System.Collections;
using System.Net.Http;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ForgeBridge
{
    /// <summary>
    /// A premier draft table runs in the server of its first player: if that player quits while the others still pick, their
    /// draft ends. Before the client quits (its own Exit, or the window's close button: both go through
    /// <see cref="Application.wantsToQuit"/>), the client's popup asks whether to quit anyway. The count comes from the local
    /// server (<c>/premier/host</c> on the art server's port), polled in the background; killing the process is not covered.
    /// </summary>
    public static class HostQuitGuard
    {
        private static readonly HttpClient http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
        private static volatile int othersDrafting;
        private static bool confirmed, asking;

        /// <summary>Coroutine (started by LocalStartup): keeps the number of people depending on this server's table.</summary>
        public static IEnumerator Watch()
        {
            while (true)
            {
                var get = http.GetStringAsync("http://127.0.0.1:" + CardArt.Port + "/premier/host");
                while (!get.IsCompleted) yield return null;
                try { othersDrafting = get.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? (int)JObject.Parse(get.Result)["othersDrafting"] : 0; }
                catch (Exception) { othersDrafting = 0; }
                yield return new WaitForSeconds(othersDrafting > 0 ? 2f : 5f);
            }
        }

        /// <summary>Application.wantsToQuit: false (and the question) while people still draft at this player's table.</summary>
        public static bool WantsToQuit()
        {
            Log.Info("quit requested:\n" + Environment.StackTrace);
            int n = othersDrafting;
            if (confirmed || n <= 0 || SystemMessageManager.Instance == null) return true;
            if (asking) return false;
            asking = true;
            Log.Info("quit held: " + n + " player(s) still drafting at the table this client hosts");
            SystemMessageManager.Instance.ShowMessage("You are hosting this draft table",
                (n == 1 ? "1 other player is" : n + " other players are") + " still picking at your table. The table runs on your computer: if you quit now, their draft ends.\n\nQuit anyway?",
                "Keep playing", () => { asking = false; },
                "Quit", () => { asking = false; confirmed = true; Log.Info("quit confirmed while hosting"); Application.Quit(); });
            return false;
        }
    }
}
