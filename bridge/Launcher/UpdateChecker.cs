using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace MtgaForgeLauncher
{
    /// <summary>What the website says is the latest release (<c>latest.json</c>, written by tools\publish-latest.ps1).</summary>
    sealed class UpdateInfo
    {
        public DateTimeOffset? Released;
        public string Commit = "", Sha256 = "", Url = "", Notes = "";
    }

    /// <summary>
    /// Compares this package's build time (dist.json, written by package.ps1) with the website's latest release. A plain GET with no identifying
    /// data. Nothing is downloaded or run: the player is shown a link. A development copy (no dist.json) never checks.
    /// </summary>
    static class UpdateChecker
    {
        public const string Endpoint = "https://play.mtga-forge.workers.dev/latest.json";

        public static bool OwnVersion(string packageRoot, out DateTimeOffset built, out string commit)
        {
            built = default(DateTimeOffset); commit = "";
            try
            {
                string f = Path.Combine(packageRoot, "dist.json");
                if (!File.Exists(f)) return false;
                string json = File.ReadAllText(f);
                commit = Str(json, "commit");
                return DateTimeOffset.TryParse(Str(json, "builtAt"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out built);
            }
            catch { return false; }
        }

        /// <summary>The latest release, or null when the website can't be reached or has no release yet.</summary>
        public static UpdateInfo Fetch(string ownCommit)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create(Endpoint);
                req.Timeout = 6000; req.ReadWriteTimeout = 6000;
                req.UserAgent = "MTGA-Forge-Launcher/" + ownCommit;
                req.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream()))
                {
                    string json = r.ReadToEnd();
                    DateTimeOffset when;
                    var info = new UpdateInfo { Commit = Str(json, "commit"), Sha256 = Str(json, "sha256"), Url = Str(json, "url"), Notes = Str(json, "notes") };
                    if (DateTimeOffset.TryParse(Str(json, "released"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out when)) info.Released = when;
                    return info;
                }
            }
            catch { return null; }
        }

        static string Str(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? Regex.Unescape(m.Groups[1].Value) : "";
        }
    }
}
