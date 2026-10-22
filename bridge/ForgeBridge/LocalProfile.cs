using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ForgeBridge
{
    /// <summary>
    /// Who the player is: the id and name in the server's profile document (<c>data\profile.json</c>, written by the Java server
    /// before the client starts). The client only needs these two to log in; cosmetics and preferences go over the Front Door.
    /// </summary>
    public static class LocalProfile
    {
        public const string FallbackId = "LOCALPLAYER0000000000000001";

        public static string PlayerId { get; private set; } = FallbackId;
        /// <summary>Name as the client shows it: <c>Name#12345</c>.</summary>
        public static string DisplayName { get; private set; } = "Forge Player#00001";

        private static bool _loaded;

        /// <summary>Read the profile once (main thread: needs Application.dataPath). A missing or unreadable file keeps the fixed fallback identity.</summary>
        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            string file = null;
            try
            {
                // FORGEBRIDGE_DATA: the server's data folder; else the layout of the patcher: <folder>\client\MTGA_Data and <folder>\data
                string dir = Environment.GetEnvironmentVariable("FORGEBRIDGE_DATA");
                if (string.IsNullOrEmpty(dir)) dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "data"));
                file = Path.Combine(dir, "profile.json");
                if (!File.Exists(file))
                {
                    string name = Environment.GetEnvironmentVariable("FORGEBRIDGE_NAME");
                    if (!string.IsNullOrEmpty(name)) DisplayName = name + "#00001";
                    Log.Info("LocalProfile: no " + file + ", using the fixed local identity");
                    return;
                }
                JObject o = JObject.Parse(File.ReadAllText(file));
                string id = (string)o["playerId"];
                string display = (string)o["displayName"];
                string tag = (string)o["tag"];
                if (!string.IsNullOrEmpty(id)) PlayerId = id;
                if (!string.IsNullOrEmpty(display)) DisplayName = display + "#" + (string.IsNullOrEmpty(tag) ? "00001" : tag);
                Log.Info("LocalProfile: " + DisplayName + " (" + PlayerId + ") from " + file);
            }
            catch (Exception e)
            {
                Log.Info("LocalProfile: cannot read " + file + ": " + e.Message);
            }
        }
    }
}
