using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Assets.Core.Code.Doorbell;
using Newtonsoft.Json.Linq;
using Wizards.Mtga.Assets;
using Wizards.Mtga.Platforms;

namespace ForgeBridge
{
    /// <summary>
    /// Builds the Doorbell response the client would normally get from Wizards, using the manifests that are
    /// already cached on disk (Downloads/Manifest_[Category_]Hash.mtga). No network.
    /// One manifest per category: an install can hold an old one next to the current one (Steam puts back the manifests its
    /// depot shipped while MTGA has already moved on). The client resolves a raw file such as the UI text database
    /// (Raw_ClientLocalization) by taking the first matching name, so an old Localization manifest whose file is gone left
    /// every label of the game empty. Of several, the one with the fewest missing files wins, then the newest.
    /// </summary>
    public static class LocalDoorbell
    {
        private static readonly Regex Name = new Regex(@"^Manifest_(?:(?<cat>[A-Za-z]+)_)?(?<hash>[0-9a-fA-F]{32})\.mtga$");

        public static DoorbellRingResponseV2 Build(string fdUri)
        {
            string dir = PlatformContext.GetStorageContext().GetAssetBundleStoragePath();
            var list = new List<AssetBundleManifestMetadata>();
            if (Directory.Exists(dir))
            {
                var byCategory = new Dictionary<string, List<string>>();
                foreach (var f in Directory.GetFiles(dir, "Manifest_*.mtga"))
                {
                    var m = Name.Match(Path.GetFileName(f));
                    if (!m.Success) continue;
                    string cat = m.Groups["cat"].Success ? m.Groups["cat"].Value : string.Empty;
                    if (!byCategory.TryGetValue(cat, out var files)) byCategory[cat] = files = new List<string>();
                    files.Add(f);
                }
                foreach (var kv in byCategory)
                {
                    string pick = Pick(dir, kv.Key, kv.Value);
                    string hash = Name.Match(Path.GetFileName(pick)).Groups["hash"].Value;
                    list.Add(new AssetBundleManifestMetadata(AssetPriority.Automatic, kv.Key, hash));
                    Log.Info("LocalDoorbell manifest: category='" + kv.Key + "' hash=" + hash);
                }
            }
            else
            {
                Log.Error("LocalDoorbell: bundle dir missing: " + dir);
            }
            return new DoorbellRingResponseV2 { FdURI = fdUri, BundleManifests = list };
        }

        private static string Pick(string dir, string cat, List<string> files)
        {
            var scored = files.Select(f => new { File = f, Missing = MissingFiles(dir, f), Time = File.GetLastWriteTimeUtc(f) })
                              .OrderBy(x => x.Missing).ThenByDescending(x => x.Time).ToList();
            var best = scored[0];
            if (scored.Count > 1)
                Log.Info("LocalDoorbell: " + scored.Count + " manifests for category '" + cat + "' ("
                    + string.Join(", ", scored.Select(x => Path.GetFileName(x.File) + " missing " + x.Missing)) + "); using " + Path.GetFileName(best.File));
            if (best.Missing > 0)
                Log.Error("LocalDoorbell: category '" + cat + "' (" + Path.GetFileName(best.File) + ") misses " + best.Missing
                    + " file(s); start MTG Arena once so it finishes downloading, then prepare the game again in MTGA Forge");
            return best.File;
        }

        /// <summary>How many files the manifest lists that are not on disk (int.MaxValue when it can't be read).</summary>
        private static int MissingFiles(string dir, string manifest)
        {
            try
            {
                byte[] b = File.ReadAllBytes(manifest);
                if (b.Length > 2 && b[0] == 0x1f && b[1] == 0x8b)
                    using (var gz = new GZipStream(new MemoryStream(b), CompressionMode.Decompress))
                    using (var o = new MemoryStream()) { gz.CopyTo(o); b = o.ToArray(); }
                var assets = JObject.Parse(Encoding.UTF8.GetString(b).TrimStart('﻿'))["Assets"] as JArray;
                if (assets == null) return int.MaxValue;
                int missing = 0;
                foreach (var a in assets)
                {
                    string name = (string)a["Name"];
                    if (string.IsNullOrEmpty(name)) continue;
                    string type = (string)a["AssetType"];
                    if (string.IsNullOrEmpty(type)) type = "AssetBundle";
                    if (!File.Exists(Path.Combine(dir, type, name))) missing++;
                }
                return missing;
            }
            catch (Exception e)
            {
                Log.Error("LocalDoorbell: can't read " + Path.GetFileName(manifest) + ": " + e.Message);
                return int.MaxValue;
            }
        }
    }
}
