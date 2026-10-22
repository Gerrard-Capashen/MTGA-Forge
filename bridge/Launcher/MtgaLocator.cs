using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MtgaForgeLauncher
{
    /// <summary>Where an MTGA install might be. Mirrors <c>Find-MtgaInstall</c> in tools\layout.ps1 and also looks at the usual library folders of every drive.</summary>
    static class MtgaLocator
    {
        public static bool IsMtga(string dir) { return !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "MTGA.exe")); }

        public static List<string> Find()
        {
            var cands = new List<string>();

            string steam = Reg(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath") ?? Reg(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
            var libs = new List<string>();
            if (steam != null)
            {
                steam = steam.Replace('/', '\\');
                libs.Add(steam);
                try
                {
                    string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
                    if (File.Exists(vdf))
                        libs.AddRange(Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"").Cast<Match>().Select(m => m.Groups[1].Value.Replace(@"\\", @"\")));
                }
                catch { }
            }
            foreach (var l in libs) cands.Add(Path.Combine(l, @"steamapps\common\MTGA"));

            // the same places on every fixed drive (a second library is often D:\SteamLibrary)
            foreach (var d in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady))
                foreach (var rel in new[] { @"SteamLibrary\steamapps\common\MTGA", @"Steam\steamapps\common\MTGA", @"Program Files (x86)\Steam\steamapps\common\MTGA",
                                            @"Games\Steam\steamapps\common\MTGA", @"Program Files\Wizards of the Coast\MTGA", @"Program Files (x86)\Wizards of the Coast\MTGA" })
                    cands.Add(Path.Combine(d.Name, rel));

            return cands.Select(c => { try { return Path.GetFullPath(c); } catch { return null; } })
                        .Where(c => c != null && IsMtga(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        static string Reg(RegistryKey root, string key, string value)
        {
            try { using (var k = root.OpenSubKey(key)) return k == null ? null : k.GetValue(value) as string; }
            catch { return null; }
        }

        /// <summary>"Steam library" for a path inside steamapps, otherwise the plain folder kind.</summary>
        public static string Describe(string dir)
        {
            string root = Path.GetPathRoot(dir) ?? "";
            return dir.IndexOf("steamapps", StringComparison.OrdinalIgnoreCase) >= 0 ? "Steam library on " + root.TrimEnd('\\') : "Found in " + root.TrimEnd('\\');
        }

        /// <summary>Size of the install in bytes (what the copy will take), 0 when unreadable.</summary>
        public static long SizeOf(string dir)
        {
            long sum = 0;
            try { foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)) sum += f.Length; }
            catch { }
            return sum;
        }
    }
}
