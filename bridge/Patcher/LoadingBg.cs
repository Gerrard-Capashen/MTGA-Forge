using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

// Patcher loadingbg <MTGA_Data> <texture name>
// The loading screen background (AssetLoading_<set>, e.g. FRA or TRK, and its handheld twin) is a 1920x1080 BC7 texture in resources.assets.resS. The art we want
// (e.g. BackgroundFull_Queue_Play_Brawl) is a texture of the same size and format in one of the player's own downloaded asset bundles
// (Downloads\AssetBundle\Bucket_Event.BackgroundPayload_*.mtga, Unity FS with LZ4 blocks). Its raw bytes are copied over the old ones in place:
// no re-encoding, no offset moves, and nothing of Wizards' art travels with our package.
static class LoadingBg
{
    const int W = 1920, H = 1080, Size = W * H, TexBC7 = 25;
    static readonly System.Text.RegularExpressions.Regex MainPattern = new System.Text.RegularExpressions.Regex("^AssetLoading_[A-Z0-9]{3}$");   // the set code changes (FRA, TRK...)

    public static void Apply(string dataDir, string artName)
    {
        var assets = Path.Combine(dataDir, "resources.assets");
        var b = File.ReadAllBytes(assets);
        var textures = new List<BootLogo.Tex>();
        foreach (var o in Tools.Objects(b).Where(o => o.cid == 28)) textures.Add(BootLogo.ParseTexture(b, (int)o.start, (int)o.size));
        var dests = new List<BootLogo.Tex>();
        var main = textures.Where(x => MainPattern.IsMatch(x.Name)).ToList();
        if (main.Count != 1) throw new Exception("expected one AssetLoading_<set> texture, found " + main.Count);
        var Targets = new[] { main[0].Name, main[0].Name + "_handheld" };
        foreach (var name in Targets)
        {
            var t = textures.Where(x => x.Name == name).ToList();
            if (t.Count != 1) throw new Exception("expected one " + name + " texture, found " + t.Count);
            Check(t[0], "resources.assets.resS");
            dests.Add(t[0]);
        }

        var art = FindArt(Path.Combine(dataDir, "Downloads", "AssetBundle"), artName);
        using (var fs = new FileStream(assets + ".resS", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            foreach (var d in dests) BootLogo.WriteAt(fs, d.Offset, art);
        Console.WriteLine("loadingbg: " + string.Join(" and ", Targets) + " = " + artName);
    }

    static void Check(BootLogo.Tex t, string resS)
    {
        if (t.Width != W || t.Height != H || t.Format != TexBC7 || t.Size != Size) throw new Exception(t.Name + " is not 1920x1080 BC7 with one mip any more");
        if (!t.StreamPath.EndsWith(resS)) throw new Exception(t.Name + " is not streamed from " + resS);
    }

    static byte[] FindArt(string dir, string name)
    {
        if (!Directory.Exists(dir)) throw new Exception("no downloaded asset bundles (" + dir + "): start MTGA once so it downloads its assets");
        var needle = Encoding.UTF8.GetBytes(name);
        foreach (var f in Directory.GetFiles(dir, "Bucket_Event.BackgroundPayload_*.mtga"))
        {
            var bundle = Bundle.Open(f);
            var serialized = bundle.Nodes.FirstOrDefault(n => !n.Name.EndsWith(".resS"));
            if (serialized.Name == null) continue;
            var sf = bundle.Read(serialized.Offset, (int)serialized.Size);
            if (Find(sf, needle) < 0) continue;      // cheap: the name is in the file's own object data
            foreach (var o in Tools.Objects(sf).Where(o => o.cid == 28))
            {
                var t = BootLogo.ParseTexture(sf, (int)o.start, (int)o.size);
                if (t.Name != name) continue;
                Check(t, ".resS");
                var node = bundle.Nodes.FirstOrDefault(n => n.Name.EndsWith(".resS") && t.StreamPath.EndsWith(n.Name));
                if (node.Name == null) throw new Exception(name + " streams from " + t.StreamPath + ", which is not in " + Path.GetFileName(f));
                return bundle.Read(node.Offset + t.Offset, (int)t.Size);
            }
        }
        throw new Exception("texture " + name + " not found in " + dir);
    }

    static int Find(byte[] b, byte[] pat)
    {
        for (int i = 0; i <= b.Length - pat.Length; i++)
        {
            int j = 0;
            while (j < pat.Length && b[i + j] == pat[j]) j++;
            if (j == pat.Length) return i;
        }
        return -1;
    }
}

// Unity FS (asset bundle) container, version 6+: header, LZ4/none compressed block table, data blocks, nodes (the files inside).
class Bundle
{
    public struct Node { public long Offset, Size; public string Name; }
    public List<Node> Nodes = new List<Node>();
    string path;
    long dataStart;
    List<(int usize, int csize, int comp)> blocks = new List<(int, int, int)>();

    static uint Be32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
    static long Be64(byte[] b, int o) => ((long)Be32(b, o) << 32) | Be32(b, o + 4);

    public static Bundle Open(string path)
    {
        var res = new Bundle { path = path };
        byte[] head;
        using (var fs = File.OpenRead(path)) { head = new byte[Math.Min(512, fs.Length)]; fs.Read(head, 0, head.Length); }
        if (Encoding.ASCII.GetString(head, 0, 7) != "UnityFS") throw new Exception(Path.GetFileName(path) + " is not a Unity FS bundle");
        int p = 8;
        uint version = Be32(head, p); p += 4;
        if (version < 6) throw new Exception("unsupported bundle version " + version);
        p = Array.IndexOf(head, (byte)0, p) + 1;      // unity version
        p = Array.IndexOf(head, (byte)0, p) + 1;      // revision
        p += 8;                                       // total size
        int csize = (int)Be32(head, p), usize = (int)Be32(head, p + 4); uint flags = Be32(head, p + 8);
        p += 12;
        if ((flags & 0x80) != 0) throw new Exception("block table at the end of the bundle: not handled");
        p = (p + 15) & ~15;
        var info = Decompress(ReadAt(path, p, csize), (int)(flags & 0x3f), usize);
        res.dataStart = p + csize;
        if ((flags & 0x200) != 0) res.dataStart = (res.dataStart + 15) & ~15;

        int q = 16;                                   // block table hash
        int n = (int)Be32(info, q); q += 4;
        for (int i = 0; i < n; i++, q += 10) res.blocks.Add(((int)Be32(info, q), (int)Be32(info, q + 4), (info[q + 8] << 8 | info[q + 9]) & 0x3f));
        n = (int)Be32(info, q); q += 4;
        for (int i = 0; i < n; i++)
        {
            long off = Be64(info, q), size = Be64(info, q + 8); q += 20;
            int e = Array.IndexOf(info, (byte)0, q);
            res.Nodes.Add(new Node { Offset = off, Size = size, Name = Encoding.UTF8.GetString(info, q, e - q) });
            q = e + 1;
        }
        return res;
    }

    // bytes [off, off + len) of the decompressed data, decompressing only the blocks they touch
    public byte[] Read(long off, int len)
    {
        var res = new byte[len];
        long u = 0, c = dataStart;
        foreach (var bl in blocks)
        {
            if (u + bl.usize > off && u < off + len)
            {
                var data = Decompress(ReadAt(path, c, bl.csize), bl.comp, bl.usize);
                long from = Math.Max(off, u), to = Math.Min(off + len, u + bl.usize);
                Buffer.BlockCopy(data, (int)(from - u), res, (int)(from - off), (int)(to - from));
            }
            u += bl.usize; c += bl.csize;
            if (u >= off + len) break;
        }
        return res;
    }

    static byte[] ReadAt(string path, long off, int len)
    {
        using (var fs = File.OpenRead(path)) { var d = new byte[len]; fs.Position = off; int n = 0; while (n < len) { int r = fs.Read(d, n, len - n); if (r <= 0) throw new Exception("bundle ends early"); n += r; } return d; }
    }

    static byte[] Decompress(byte[] src, int mode, int usize)
    {
        if (mode == 0) return src;
        if (mode != 2 && mode != 3) throw new Exception("bundle compression " + mode + " (LZMA) is not handled");
        var dst = new byte[usize];
        int sp = 0, dp = 0;
        while (sp < src.Length && dp < usize)
        {
            int token = src[sp++], lit = token >> 4;
            if (lit == 15) { int x; do { x = src[sp++]; lit += x; } while (x == 255); }
            Buffer.BlockCopy(src, sp, dst, dp, lit); sp += lit; dp += lit;
            if (dp >= usize) break;
            int off = src[sp] | src[sp + 1] << 8; sp += 2;
            int ml = token & 15;
            if (ml == 15) { int x; do { x = src[sp++]; ml += x; } while (x == 255); }
            ml += 4;
            if (off == 0 || off > dp) throw new Exception("corrupt LZ4 block");
            for (int i = 0; i < ml; i++, dp++) dst[dp] = dst[dp - off];
        }
        if (dp != usize) throw new Exception("LZ4 block ended at " + dp + " of " + usize);
        return dst;
    }
}
