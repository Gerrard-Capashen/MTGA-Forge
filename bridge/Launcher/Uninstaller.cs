using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace MtgaForgeLauncher
{
    /// <summary>One thing the uninstall can remove, with what it holds and whether it is selected.</summary>
    sealed class UninstallItem : Bindable
    {
        bool isChecked;
        public string Title { get; set; }
        public string Detail { get; set; }
        public List<string> Dirs { get; } = new List<string>();
        public List<string> Files { get; } = new List<string>();
        public string RegistryKey { get; set; }

        /// <summary>Extra removal step that returns a message on failure (the firewall rule).</summary>
        public Func<string> Custom { get; set; }
        public long Bytes { get; set; }
        public bool Exists { get; set; }
        public bool IsChecked { get { return isChecked; } set { Set(ref isChecked, value); } }
        public string SizeText { get { return !Exists ? "not found" : Bytes <= 0 ? "" : Uninstaller.Format(Bytes); } }
    }

    /// <summary>
    /// Removes what MTGA Forge put on this PC and nothing else. Only named parts are deleted (never "the folder"): the game's own
    /// MTGA install and account are never touched, and Forge's shared profile (%APPDATA%\Forge) and art cache are left for a Forge you may use.
    /// </summary>
    static class Uninstaller
    {
        const string UnityCompany = "Forge Bridge Project";     // the "company" the patched client writes under (app.info)

        public static string Format(long bytes)
        {
            const double GB = 1024.0 * 1024 * 1024, MB = 1024.0 * 1024;
            return bytes >= GB ? string.Format("{0:0.0} GB", bytes / GB) : string.Format("{0:0} MB", Math.Max(1, bytes / MB));
        }

        /// <summary>Why this folder must not be cleaned, or null.</summary>
        public static string Guard(string folder, string source)
        {
            try
            {
                string full = Path.GetFullPath(folder).TrimEnd('\\');
                if (Path.GetPathRoot(full).TrimEnd('\\').Equals(full, StringComparison.OrdinalIgnoreCase)) return "That is a whole drive.";
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
                if (full.Equals(profile, StringComparison.OrdinalIgnoreCase) || full.IndexOf("steamapps", StringComparison.OrdinalIgnoreCase) >= 0) return "That folder is not ours to clean.";
                if (!string.IsNullOrEmpty(source) && full.Equals(source.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return "That is the MTGA install.";
                return null;
            }
            catch (Exception e) { return e.Message; }
        }

        /// <summary>The folder holds MTGA Forge when it has a patched client, a profile or server logs.</summary>
        static bool LooksOurs(string folder)
        {
            return Directory.Exists(Path.Combine(folder, "client", "forge-patch")) || File.Exists(Path.Combine(folder, "data", "profile.json"))
                || File.Exists(Path.Combine(folder, "data", "identity.json"))
                || (Directory.Exists(Path.Combine(folder, "logs")) && Directory.EnumerateFiles(Path.Combine(folder, "logs"), "server-*").Any());
        }

        public static List<UninstallItem> Plan(string folder, string source)
        {
            var items = new List<UninstallItem>();
            bool ours = Guard(folder, source) == null && Directory.Exists(folder) && LooksOurs(folder);

            var copy = new UninstallItem { Title = "Patched game copy", Detail = "The copy of MTGA that talks to Forge. Your MTGA install stays.", IsChecked = true };
            var logs = new UninstallItem { Title = "Logs", Detail = "Server logs from earlier sessions.", IsChecked = true };
            var data = new UninstallItem { Title = "Decks, profile and identity", Detail = "Your decks, your name and tag, your friends. Back up your identity first if you may come back.", IsChecked = false };
            if (ours)
            {
                if (Directory.Exists(Path.Combine(folder, "client", "forge-patch"))) copy.Dirs.Add(Path.Combine(folder, "client"));
                if (Directory.Exists(Path.Combine(folder, "logs"))) logs.Dirs.Add(Path.Combine(folder, "logs"));
                if (Directory.Exists(Path.Combine(folder, "data"))) data.Dirs.Add(Path.Combine(folder, "data"));
            }

            var prefs = new UninstallItem { Title = "Game preferences", Detail = "Settings and the game's own log, kept by Windows outside the folder.", IsChecked = true };
            string localLow = Path.Combine(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), "LocalLow", UnityCompany);
            if (Directory.Exists(localLow)) prefs.Dirs.Add(localLow);
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\" + UnityCompany)) if (k != null) prefs.RegistryKey = @"Software\" + UnityCompany;

            var own = new UninstallItem { Title = "Launcher settings", Detail = "What this window remembers: folder, name, options.", IsChecked = true };
            foreach (var f in new[] { "ui.ini", "config.json" }) if (File.Exists(Path.Combine(Settings.Home, f))) own.Files.Add(Path.Combine(Settings.Home, f));

            var fw = new UninstallItem { Title = "Firewall rule", Detail = "The rule that blocks the game from the internet. Windows asks for administrator approval to remove it.", IsChecked = true };
            if (Firewall.Exists(folder)) fw.Custom = () => Firewall.Set(folder, false);

            foreach (var i in new[] { copy, logs, data, prefs, own, fw })
            {
                i.Exists = i.Dirs.Count + i.Files.Count > 0 || i.RegistryKey != null || i.Custom != null;
                if (!i.Exists) i.IsChecked = false;
                items.Add(i);
            }
            return items;
        }

        /// <summary>Fills in the sizes (slow for the game copy, so it runs off the UI thread).</summary>
        public static void Measure(IEnumerable<UninstallItem> items)
        {
            foreach (var i in items)
            {
                long sum = 0;
                foreach (var d in i.Dirs) sum += MtgaLocator.SizeOf(d);
                foreach (var f in i.Files) try { sum += new FileInfo(f).Length; } catch { }
                i.Bytes = sum;
            }
        }

        /// <summary>Removes the selected items; returns one line per problem (empty when everything went).</summary>
        public static List<string> Remove(IEnumerable<UninstallItem> items, string folder, Action<string> status)
        {
            var problems = new List<string>();
            foreach (var i in items.Where(x => x.IsChecked && x.Exists))
            {
                status("Removing " + i.Title.ToLowerInvariant() + "...");
                foreach (var d in i.Dirs) Try(() => DeleteTree(d), d, problems);
                foreach (var f in i.Files) Try(() => File.Delete(f), f, problems);
                if (i.Custom != null) { string err = null; Try(() => err = i.Custom(), i.Title, problems); if (err != null) problems.Add(err); }
                if (i.RegistryKey != null) Try(() => Registry.CurrentUser.DeleteSubKeyTree(i.RegistryKey, false), "HKCU\\" + i.RegistryKey, problems);
            }
            // leave no empty shells behind
            foreach (var dir in new[] { folder, Settings.Home }) Try(() => { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }, dir, problems);
            return problems;
        }

        static void Try(Action a, string what, List<string> problems)
        {
            try { a(); } catch (Exception e) { problems.Add(what + ": " + e.Message); }
        }

        /// <summary>Directory.Delete refuses read-only files, which a copied game has.</summary>
        static void DeleteTree(string path)
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            Directory.Delete(path, true);
        }
    }
}
