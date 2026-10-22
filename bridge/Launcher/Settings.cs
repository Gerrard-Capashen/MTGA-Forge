using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MtgaForgeLauncher
{
    /// <summary>What the window remembers between starts (<c>%LOCALAPPDATA%\MTGA-Forge\ui.ini</c>); same keys as the earlier launcher.</summary>
    sealed class Settings
    {
        /// <summary>Small files of the launcher itself; the game copy lives in <see cref="Folder"/>.</summary>
        public static readonly string Home = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? ".", "MTGA-Forge");

        /// <summary>
        /// Default place for the patched copy: the per-user programs folder (%LOCALAPPDATA%\Programs, where VS Code and Discord install).
        /// Not Program Files: Windows does not let a normal program write there, and the patched game writes into its own folder.
        /// </summary>
        public static string DefaultFolder { get { return Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? ".", "Programs", "MTGA Forge"); } }

        readonly string iniPath = Path.Combine(Home, "ui.ini");

        public string Name = "", Code = "", Folder = "", Source = "";
        public bool Offline, ConsoleOpen;

        public void Load()
        {
            var kv = new Dictionary<string, string>();
            try
            {
                if (File.Exists(iniPath))
                    foreach (var line in File.ReadAllLines(iniPath))
                    {
                        int i = line.IndexOf('=');
                        if (i > 0) kv[line.Substring(0, i)] = line.Substring(i + 1);
                    }
            }
            catch { }
            Func<string, string> get = k => kv.ContainsKey(k) ? kv[k] : "";
            Name = get("name"); Code = get("code"); Folder = get("folder"); Source = get("source");
            Offline = get("offline") == "1"; ConsoleOpen = get("console") == "1";

            if (Folder == "") Folder = FolderOfCommandLineLauncher();
            if (Folder == "") Folder = DefaultFolder;

            string n, id, tag;
            if (Profile.Read(Folder, out n, out id, out tag) && n != "") Name = n;
            else if (Name == "" || Name == "Forge Player") Name = RandomName();   // first start: a name of their own (the game adds #12345)
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Home);
                File.WriteAllLines(iniPath, new[]
                {
                    "name=" + Name, "code=" + Code, "offline=" + (Offline ? "1" : "0"),
                    "folder=" + Folder, "source=" + Source, "console=" + (ConsoleOpen ? "1" : "0"),
                });
            }
            catch { }
        }

        /// <summary>The folder MTGA-Forge.cmd / launcher.ps1 remembered.</summary>
        static string FolderOfCommandLineLauncher()
        {
            try
            {
                string cfg = Path.Combine(Home, "config.json");
                if (!File.Exists(cfg)) return "";
                var m = Regex.Match(File.ReadAllText(cfg), "\"home\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                return m.Success ? m.Groups[1].Value.Replace("\\\\", "\\") : "";
            }
            catch { return ""; }
        }

        static readonly string[] Adjectives = { "Quick", "Brave", "Silent", "Golden", "Crimson", "Azure", "Mystic", "Wandering", "Clever", "Fierce", "Gentle", "Hidden", "Ancient", "Lucky", "Stormy", "Verdant" };
        static readonly string[] Nouns = { "Otter", "Falcon", "Golem", "Sphinx", "Wurm", "Druid", "Rogue", "Mage", "Knight", "Phoenix", "Raven", "Badger", "Dragon", "Fox", "Elk", "Lynx" };

        static string RandomName()
        {
            var r = new Random();
            return Adjectives[r.Next(Adjectives.Length)] + Nouns[r.Next(Nouns.Length)];
        }
    }

    /// <summary>The player's profile of an install (<c>data\profile.json</c>, written by the server) and the identity backup.</summary>
    static class Profile
    {
        public static bool Read(string folder, out string name, out string id, out string tag)
        {
            name = id = tag = "";
            try
            {
                string file = Path.Combine(folder, "data", "profile.json");
                if (!File.Exists(file)) return false;
                string json = File.ReadAllText(file);
                name = JsonString(json, "displayName"); id = JsonString(json, "playerId"); tag = JsonString(json, "tag");
                return id != "";
            }
            catch { return false; }
        }

        static string JsonString(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? Regex.Unescape(m.Groups[1].Value) : "";
        }

        /// <summary>"Player ID 791602cb, shown as Name#85191", or a hint before the first game.</summary>
        public static string Note(string folder)
        {
            string n, id, tag;
            if (!Read(folder, out n, out id, out tag)) return "A player ID is made the first time you start the game.";
            return "Player ID " + (id.Length > 8 ? id.Substring(0, 8) : id) + (tag != "" ? ", shown as " + n + "#" + tag : "");
        }

        public static bool HasIdentity(string folder) { return File.Exists(Path.Combine(folder, "data", "identity.json")); }

        /// <summary>Copies the device key and profile into <c>target\MTGA-Forge-identity</c>; returns that folder.</summary>
        public static string Backup(string folder, string target)
        {
            string data = Path.Combine(folder, "data"), dir = Path.Combine(target, "MTGA-Forge-identity");
            Directory.CreateDirectory(dir);
            File.Copy(Path.Combine(data, "identity.json"), Path.Combine(dir, "identity.json"), true);
            string prof = Path.Combine(data, "profile.json");
            if (File.Exists(prof)) File.Copy(prof, Path.Combine(dir, "profile.json"), true);
            return dir;
        }

        /// <summary>Puts a backup back; accepts the backup folder or the folder that contains it. Returns false when it holds no identity.</summary>
        public static bool Restore(string folder, string source)
        {
            string src = source;
            if (!File.Exists(Path.Combine(src, "identity.json")) && File.Exists(Path.Combine(src, "MTGA-Forge-identity", "identity.json")))
                src = Path.Combine(src, "MTGA-Forge-identity");
            if (!File.Exists(Path.Combine(src, "identity.json"))) return false;
            string data = Path.Combine(folder, "data");
            Directory.CreateDirectory(data);
            File.Copy(Path.Combine(src, "identity.json"), Path.Combine(data, "identity.json"), true);
            if (File.Exists(Path.Combine(src, "profile.json"))) File.Copy(Path.Combine(src, "profile.json"), Path.Combine(data, "profile.json"), true);
            return true;
        }
    }
}
