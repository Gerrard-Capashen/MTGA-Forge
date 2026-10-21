using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Assets.Core.Code.Doorbell;
using Wizards.Mtga.Assets;
using Wizards.Mtga.Platforms;

namespace ForgeBridge
{
    /// <summary>
    /// Builds the Doorbell response the client would normally get from Wizards, using the manifests that are
    /// already cached on disk (Downloads/Manifest_[Category_]Hash.mtga). No network.
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
                foreach (var f in Directory.GetFiles(dir, "Manifest_*.mtga"))
                {
                    var m = Name.Match(Path.GetFileName(f));
                    if (!m.Success) continue;
                    string cat = m.Groups["cat"].Success ? m.Groups["cat"].Value : string.Empty;
                    list.Add(new AssetBundleManifestMetadata(AssetPriority.Automatic, cat, m.Groups["hash"].Value));
                    Log.Info("LocalDoorbell manifest: category='" + cat + "' hash=" + m.Groups["hash"].Value);
                }
            }
            else
            {
                Log.Error("LocalDoorbell: bundle dir missing: " + dir);
            }
            return new DoorbellRingResponseV2 { FdURI = fdUri, BundleManifests = list };
        }
    }
}
