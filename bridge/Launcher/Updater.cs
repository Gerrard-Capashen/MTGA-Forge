using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace MtgaForgeLauncher
{
    /// <summary>One part of a release (tools\update-manifest.ps1): a folder of the package, or (app) a list of top-level files.</summary>
    sealed class UpdatePart
    {
        public string Name = "", Path = "", Hash = "", Url = "", Sha256 = "";
        public long Size;
        public List<string> Files;      // only for the top-level part
    }

    /// <summary>A release as its signed update.json describes it.</summary>
    sealed class UpdateRelease
    {
        public string Version = "", Commit = "", Notes = "", DistJson = "";
        public DateTimeOffset BuiltAt;
        public List<UpdatePart> Parts = new List<UpdatePart>();
    }

    /// <summary>
    /// Auto-update. On start the launcher reads the latest release's update.json (+ .sig) from GitHub, checks its signature against
    /// <see cref="UpdateKey"/>, and compares each part's content hash with components.json (what is installed). Installing downloads the
    /// changed parts, checks each zip's SHA-256 and the unpacked content's hash, then swaps the parts in (rolling back on any failure)
    /// and writes the new components.json and dist.json. Nothing is replaced while the game runs: the launcher only offers this before Start.
    /// </summary>
    static class Updater
    {
        public const string DefaultManifestUrl = "https://github.com/Gerrard-Capashen/MTGA-Forge/releases/latest/download/update.json";

        /// <summary>MTGA_FORGE_UPDATE_URL overrides it for tests (a local server); the signature is required either way.</summary>
        public static string ManifestUrl
        {
            get { var e = Environment.GetEnvironmentVariable("MTGA_FORGE_UPDATE_URL"); return string.IsNullOrEmpty(e) ? DefaultManifestUrl : e; }
        }

        const string StagingName = ".update";
        public static string ExePath { get { return Process.GetCurrentProcess().MainModule.FileName; } }

        // ------------------------------------------------------------------------------------------ the update list
        /// <summary>The latest release, or null (error says why: offline, no update list yet, a bad signature).</summary>
        public static UpdateRelease Fetch(string userAgent, out string error, out bool missing)
        {
            error = ""; missing = false;
            try
            {
                string url = ManifestUrl;
                if (!Allowed(url)) { error = "the update address is not https: " + url; return null; }
                byte[] json = Get(url, userAgent);
                string sig = Encoding.ASCII.GetString(Get(url + ".sig", userAgent)).Trim();
                if (!SignatureValid(json, sig)) { error = "the update list's signature is not valid (it was not made by the MTGA Forge release key)"; return null; }
                return Parse(json);
            }
            catch (WebException e)
            {
                var r = e.Response as HttpWebResponse;
                if (r != null && r.StatusCode == HttpStatusCode.NotFound) { missing = true; error = "the latest release has no update list"; }
                else error = e.Message;
                return null;
            }
            catch (Exception e) { error = e.Message; return null; }
        }

        static bool Allowed(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            return u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback);
        }

        static HttpWebRequest Request(string url, string userAgent)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = 20000; req.ReadWriteTimeout = 60000;
            req.UserAgent = userAgent;
            req.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            return req;
        }

        static byte[] Get(string url, string userAgent)
        {
            using (var resp = (HttpWebResponse)Request(url, userAgent).GetResponse())
            using (var s = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                if (!Allowed(resp.ResponseUri.AbsoluteUri)) throw new IOException("redirected to a non-https address");
                return ms.ToArray();
            }
        }

        public static bool SignatureValid(byte[] data, string base64Signature)
        {
            byte[] sig;
            try { sig = Convert.FromBase64String(base64Signature); } catch (FormatException) { return false; }
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.FromXmlString(UpdateKey.PublicKeyXml);
                return rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
        }

        static UpdateRelease Parse(byte[] json)
        {
            var js = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
            var o = (Dictionary<string, object>)js.DeserializeObject(Encoding.UTF8.GetString(json));
            if (Convert.ToInt32(o["schema"]) != 1) throw new InvalidDataException("unknown update list format " + o["schema"]);
            var r = new UpdateRelease
            {
                Version = S(o, "version"), Commit = S(o, "commit"), Notes = S(o, "notes"),
                BuiltAt = DateTimeOffset.Parse(S(o, "builtAt"), System.Globalization.CultureInfo.InvariantCulture),
                DistJson = js.Serialize(o["dist"]),
            };
            foreach (Dictionary<string, object> c in (object[])o["components"])
            {
                var p = new UpdatePart { Name = S(c, "name"), Path = S(c, "path"), Hash = S(c, "hash"), Url = S(c, "url"), Sha256 = S(c, "sha256"), Size = Convert.ToInt64(c["size"]) };
                object files;
                if (c.TryGetValue("files", out files) && files is object[]) p.Files = ((object[])files).Select(f => (string)f).ToList();
                if (p.Name == "" || p.Hash.Length != 64 || !Allowed(p.Url) || SafeRelative(p.Path) == null) throw new InvalidDataException("bad part in the update list: " + p.Name);
                if (p.Path == "" && (p.Files == null || p.Files.Any(f => SafeRelative(f) == null || f.Contains("/")))) throw new InvalidDataException("bad file list for part " + p.Name);
                r.Parts.Add(p);
            }
            return r;
        }

        static string S(Dictionary<string, object> o, string k) { object v; return o.TryGetValue(k, out v) && v != null ? v.ToString() : ""; }

        /// <summary>A path inside the package ('/' separated, no '..', not rooted), as a Windows relative path; null if unsafe.</summary>
        static string SafeRelative(string p)
        {
            if (p == null) return null;
            if (p == "") return "";
            if (p.StartsWith("/") || p.Contains("\\") || p.Contains(":") || p.Split('/').Any(x => x == "" || x == "." || x == "..")) return null;
            return p.Replace('/', '\\');
        }

        // ------------------------------------------------------------------------------------------ what is installed
        /// <summary>Installed part hashes from components.json (written by package.ps1 and by every update).</summary>
        public static Dictionary<string, string> Installed(string root)
        {
            var result = new Dictionary<string, string>();
            try
            {
                string f = Path.Combine(root, "components.json");
                if (!File.Exists(f)) return result;
                var o = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(f));
                foreach (var kv in (Dictionary<string, object>)o["components"]) result[kv.Key] = kv.Value as string ?? "";
            }
            catch { }
            return result;
        }

        /// <summary>The parts of <paramref name="r"/> that differ from what is installed (or are missing on disk).</summary>
        public static List<UpdatePart> Changed(UpdateRelease r, string root)
        {
            var have = Installed(root);
            return r.Parts.Where(p =>
            {
                string h;
                if (!have.TryGetValue(p.Name, out h) || h != p.Hash) return true;
                if (p.Path == "") return p.Files.Any(f => !File.Exists(Path.Combine(root, f)));
                return !Directory.Exists(Path.Combine(root, SafeRelative(p.Path)));
            }).ToList();
        }

        /// <summary>SHA-256 over the sorted lines "path\tsha256\n" (the same as tools\update-manifest.ps1 TreeHash).</summary>
        public static string TreeHash(string dir, List<string> files)
        {
            IEnumerable<string> rels = files != null
                ? files
                : Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(f => f.Substring(dir.Length + 1).Replace('\\', '/'));
            var lines = new List<string>();
            using (var sha = SHA256.Create())
                foreach (var rel in rels)
                    using (var fs = File.OpenRead(Path.Combine(dir, rel.Replace('/', '\\'))))
                        lines.Add(rel + "\t" + Hex(sha.ComputeHash(fs)) + "\n");
            lines.Sort(StringComparer.Ordinal);
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(new UTF8Encoding(false).GetBytes(string.Concat(lines))));
        }

        static string Hex(byte[] b) { var sb = new StringBuilder(b.Length * 2); foreach (var x in b) sb.Append(x.ToString("x2")); return sb.ToString(); }

        // ------------------------------------------------------------------------------------------ installing
        /// <summary>
        /// Downloads, checks and installs <paramref name="parts"/> of <paramref name="r"/> into the package at <paramref name="root"/>.
        /// Returns true when the launcher itself changed (restart it). Throws with a readable message on any failure; the package is
        /// then as it was.
        /// </summary>
        public static bool Install(UpdateRelease r, List<UpdatePart> parts, string root, string userAgent, Action<string> progress)
        {
            string staging = Path.Combine(root, StagingName);
            DeleteDir(staging);
            Directory.CreateDirectory(staging);
            long total = Math.Max(1, parts.Sum(p => p.Size)), done = 0;

            // 1. download and check everything first: nothing on disk changes until every part is here and verified
            foreach (var p in parts)
            {
                string zip = Path.Combine(staging, p.Name + ".zip");
                long before = done;
                Download(p.Url, zip, userAgent, n => progress(string.Format("Downloading the update: {0} of {1} MB", Mb(before + n), Mb(total))));
                done += p.Size;
                var fi = new FileInfo(zip);
                if (fi.Length != p.Size || FileSha(zip) != p.Sha256) throw new IOException("the download of " + p.Name + " is damaged (size or SHA-256 differs); try again");
                progress("Checking " + p.Name + "...");
                string dir = Path.Combine(staging, "new", p.Name);
                Extract(zip, dir);
                File.Delete(zip);
                if (TreeHash(dir, p.Files) != p.Hash) throw new IOException("the content of " + p.Name + " does not match the update list; nothing was changed");
            }

            // 2. swap: each old part moves aside first, so a failure can put everything back
            progress("Installing the update...");
            var undo = new Stack<Action>();
            bool appChanged = false;
            string exe = ExePath;
            try
            {
                foreach (var p in parts.OrderBy(p => p.Path == "" ? 1 : 0))      // the launcher's own files last
                {
                    string from = Path.Combine(staging, "new", p.Name);
                    if (p.Path == "")
                    {
                        appChanged = true;
                        foreach (var f in p.Files)
                        {
                            string dst = Path.Combine(root, f), src = Path.Combine(from, f);
                            string aside = string.Equals(dst, exe, StringComparison.OrdinalIgnoreCase)
                                ? dst + ".old"                                                   // a running exe can be renamed, not overwritten
                                : Path.Combine(staging, "old", p.Name, f);
                            if (File.Exists(dst))
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(aside));
                                if (File.Exists(aside)) File.Delete(aside);
                                File.Move(dst, aside);
                                undo.Push(() => { if (File.Exists(dst)) File.Delete(dst); File.Move(aside, dst); });
                            }
                            else undo.Push(() => { if (File.Exists(dst)) File.Delete(dst); });
                            File.Move(src, dst);
                        }
                    }
                    else
                    {
                        string dst = Path.Combine(root, SafeRelative(p.Path)), aside = Path.Combine(staging, "old", p.Name);
                        if (Directory.Exists(dst))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(aside));
                            Directory.Move(dst, aside);
                            undo.Push(() => { if (Directory.Exists(dst)) Directory.Delete(dst, true); Directory.Move(aside, dst); });
                        }
                        else undo.Push(() => { if (Directory.Exists(dst)) Directory.Delete(dst, true); });
                        Directory.CreateDirectory(Path.GetDirectoryName(dst));
                        Directory.Move(from, dst);
                    }
                }
            }
            catch (Exception e)
            {
                while (undo.Count > 0) { try { undo.Pop()(); } catch { } }
                throw new IOException("the update could not replace the old files (" + e.Message + "). Close any MTGA Forge game that is still running and try again. Nothing was changed.");
            }

            // 3. record what is installed now
            var state = new Dictionary<string, object>
            {
                { "schema", 1 }, { "commit", r.Commit }, { "builtAt", r.BuiltAt.ToString("o") },
                { "components", r.Parts.ToDictionary(p => p.Name, p => (object)p.Hash) },
            };
            var js = new JavaScriptSerializer();
            File.WriteAllText(Path.Combine(root, "components.json"), js.Serialize(state), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "dist.json"), r.DistJson, new UTF8Encoding(false));
            DeleteDir(staging);
            return appChanged;
        }

        static void Download(string url, string file, string userAgent, Action<long> onBytes)
        {
            if (!Allowed(url)) throw new IOException("refused a non-https download: " + url);
            using (var resp = (HttpWebResponse)Request(url, userAgent).GetResponse())
            {
                if (!Allowed(resp.ResponseUri.AbsoluteUri)) throw new IOException("redirected to a non-https address");
                using (var s = resp.GetResponseStream())
                using (var fs = File.Create(file))
                {
                    var buf = new byte[1 << 16];
                    long n = 0; int read; var tick = Stopwatch.StartNew();
                    while ((read = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        fs.Write(buf, 0, read); n += read;
                        if (tick.ElapsedMilliseconds > 300) { onBytes(n); tick.Restart(); }
                    }
                    onBytes(n);
                }
            }
        }

        /// <summary>Unzips into <paramref name="dir"/>, refusing any entry that would land outside it.</summary>
        static void Extract(string zip, string dir)
        {
            string full = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            Directory.CreateDirectory(full);
            using (var za = ZipFile.OpenRead(zip))
                foreach (var e in za.Entries)
                {
                    string target = Path.GetFullPath(Path.Combine(full, e.FullName.Replace('/', '\\')));
                    if (!target.StartsWith(full, StringComparison.OrdinalIgnoreCase)) throw new IOException("unsafe path in the update: " + e.FullName);
                    if (e.Name == "") { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    e.ExtractToFile(target, true);
                }
        }

        static string FileSha(string f) { using (var sha = SHA256.Create()) using (var fs = File.OpenRead(f)) return Hex(sha.ComputeHash(fs)); }
        static string Mb(long b) { return (b / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture); }

        static void DeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }

        /// <summary>At start: remove what an update left behind (the previous exe, the staging folder).</summary>
        public static void CleanUp(string root)
        {
            DeleteDir(Path.Combine(root, StagingName));
            string old = ExePath + ".old";
            for (int i = 0; i < 20 && File.Exists(old); i++)     // the previous process may still be exiting
            {
                try { File.Delete(old); } catch { System.Threading.Thread.Sleep(250); }
            }
        }

        /// <summary>Starts the (new) launcher again and tells the caller to exit.</summary>
        public static void Restart()
        {
            Process.Start(new ProcessStartInfo(ExePath, "--updated") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(ExePath) });
        }
    }
}
