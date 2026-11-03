using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;

// Patcher bootlogo <MTGA_Data> <logo.png>
// The Arena logo of the boot/login screen exists twice in resources.assets: the default one (MTGA_Logo_White) is a sprite inside the
// "Login" sprite atlas (DXT5), the set-promo one (AssetLoad_Logo_<set>, e.g. FRA or TRK) is a texture of its own (RGBA32). Both are 419x173. The pixels
// live in resources.assets.resS, so nothing in resources.assets moves: the logo is written into the raw texture data, in place.
// The original bytes of the two places go to resources.assets.resS.bootlogo.orig first (a few hundred KB, not a copy of the 80 MB resS);
// every run starts from them, so running again, or with another logo, never degrades the atlas (DXT5 is lossy).
static class BootLogo
{
    const int W = 419, H = 173;
    const int SpriteAtlasClass = 687078895;
    // the set code of the promo texture changes with the set Arena promotes (FRA, TRK...), so it is found by pattern
    static readonly System.Text.RegularExpressions.Regex PromoPattern = new System.Text.RegularExpressions.Regex("^AssetLoad_Logo_[A-Z0-9]{3}$");
    const int TexRGBA32 = 4, TexDXT5 = 12;

    internal class Tex
    {
        public string Name, StreamPath;
        public int Width, Height, Format;
        public long Offset, Id; public uint Size;
    }

    public static void Apply(string dataDir, string pngPath)
    {
        var assets = Path.Combine(dataDir, "resources.assets");
        var b = File.ReadAllBytes(assets);
        var textures = new List<Tex>();
        foreach (var o in Tools.Objects(b).Where(o => o.cid == 28)) { var t = ParseTexture(b, (int)o.start, (int)o.size); t.Id = o.id; textures.Add(t); }
        var promo = textures.Where(t => PromoPattern.IsMatch(t.Name)).ToList();
        var PromoName = promo.Count == 1 ? promo[0].Name : "AssetLoad_Logo_???";
        var atlas = textures.Where(t => t.Name.StartsWith("sactx-") && t.Name.Contains("-Login-")).ToList();
        if (promo.Count != 1 || atlas.Count != 1) throw new Exception("expected one " + PromoName + " texture and one Login sprite atlas, found " + promo.Count + " / " + atlas.Count);
        var pt = promo[0]; var at = atlas[0];
        if (pt.Width != W || pt.Height != H || pt.Format != TexRGBA32 || pt.Size != (uint)(W * H * 4)) throw new Exception(PromoName + " is not 419x173 RGBA32 any more");
        if (at.Width != 512 || at.Height != 1024 || at.Format != TexDXT5 || at.Size != 512 * 1024) throw new Exception("the Login atlas is not 512x1024 DXT5 any more");
        if (pt.StreamPath != "resources.assets.resS" || at.StreamPath != pt.StreamPath) throw new Exception("textures are not in resources.assets.resS");

        var logo = LoadPng(pngPath);

        // Where the default logo sits in the atlas: the SpriteAtlas lists every packed sprite with (texture pointer, alpha texture pointer, textureRect x y w h);
        // the one rect of exactly 419x173 pointing at the Login atlas texture is MTGA_Logo_White. Unity's rows count from the bottom.
        var rect = new byte[8]; BitConverter.GetBytes(419f).CopyTo(rect, 0); BitConverter.GetBytes(173f).CopyTo(rect, 4);
        var hits = new List<(float x, float y)>();
        foreach (var o in Tools.Objects(b).Where(o => o.cid == SpriteAtlasClass))
        {
            int s = (int)o.start, e = s + (int)o.size;
            for (int i = IndexOf(b, rect, s + 32, e); i >= 0; i = IndexOf(b, rect, i + 1, e))
                if (BitConverter.ToInt32(b, i - 32) == 0 && BitConverter.ToInt64(b, i - 28) == at.Id) hits.Add((BitConverter.ToSingle(b, i - 8), BitConverter.ToSingle(b, i - 4)));
        }
        if (hits.Count != 1 || hits[0].x != Math.Floor(hits[0].x) || hits[0].y != Math.Floor(hits[0].y)) throw new Exception("expected one 419x173 sprite in the Login atlas, found " + hits.Count);
        int AtlasX = (int)hits[0].x, AtlasY = (int)hits[0].y;
        if (AtlasX < 0 || AtlasY < 0 || AtlasX + W > 512 || AtlasY + H > 1024) throw new Exception("logo rect outside the atlas");

        // blocks of the atlas touched by the logo: whole rows of blocks (128 per row, 16 bytes each), rows counted from the bottom
        const int blocksPerRow = 512 / 4, rowBytes = blocksPerRow * 16;
        int row0 = AtlasY / 4, row1 = (AtlasY + H - 1) / 4;
        long atlasOff = at.Offset + (long)row0 * rowBytes;
        int atlasLen = (row1 - row0 + 1) * rowBytes;

        var resS = assets + ".resS";
        var side = resS + ".bootlogo.orig";
        byte[] promoOrig, atlasOrig;
        using (var fs = new FileStream(resS, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            if (File.Exists(side))
            {
                var s = File.ReadAllBytes(side);
                if (s.Length != 16 + (int)pt.Size + atlasLen || BitConverter.ToInt64(s, 0) != pt.Offset || BitConverter.ToInt64(s, 8) != atlasOff)
                    throw new Exception(Path.GetFileName(side) + " does not belong to this resS (delete it only on a pristine client)");
                promoOrig = s.Skip(16).Take((int)pt.Size).ToArray();
                atlasOrig = s.Skip(16 + (int)pt.Size).ToArray();
            }
            else
            {
                promoOrig = ReadAt(fs, pt.Offset, (int)pt.Size);
                atlasOrig = ReadAt(fs, atlasOff, atlasLen);
                var s = new List<byte>();
                s.AddRange(BitConverter.GetBytes(pt.Offset)); s.AddRange(BitConverter.GetBytes(atlasOff));
                s.AddRange(promoOrig); s.AddRange(atlasOrig);
                File.WriteAllBytes(side, s.ToArray());
            }

            // promo texture: RGBA32, rows bottom-up
            var promoNew = new byte[pt.Size];
            for (int y = 0; y < H; y++)
                Buffer.BlockCopy(logo, (H - 1 - y) * W * 4, promoNew, y * W * 4, W * 4);

            // atlas: decode the touched blocks, paste the logo, encode them again
            var atlasNew = (byte[])atlasOrig.Clone();
            for (int br = row0; br <= row1; br++)
                for (int bx = 0; bx < blocksPerRow; bx++)
                {
                    int x0 = bx * 4, y0 = br * 4;
                    if (x0 + 3 < AtlasX || x0 >= AtlasX + W) continue;
                    int o = (br - row0) * rowBytes + bx * 16;
                    var px = Dxt5.Decode(atlasOrig, o);
                    for (int r = 0; r < 4; r++)
                        for (int c = 0; c < 4; c++)
                        {
                            int lx = x0 + c - AtlasX, ly = y0 + r - AtlasY;
                            if (lx < 0 || lx >= W || ly < 0 || ly >= H) continue;
                            Buffer.BlockCopy(logo, ((H - 1 - ly) * W + lx) * 4, px, (r * 4 + c) * 4, 4);
                        }
                    Dxt5.Encode(px, atlasNew, o);
                }

            WriteAt(fs, pt.Offset, promoNew);
            WriteAt(fs, atlasOff, atlasNew);
            Console.WriteLine("bootlogo: " + PromoName + " and the Login atlas logo replaced at " + AtlasX + "," + AtlasY + ", " + (row1 - row0 + 1) + " block rows");
        }
    }

    static int IndexOf(byte[] b, byte[] pat, int from, int to)
    {
        for (int i = from; i <= to - pat.Length; i++)
        {
            int j = 0;
            while (j < pat.Length && b[i + j] == pat[j]) j++;
            if (j == pat.Length) return i;
        }
        return -1;
    }

    internal static byte[] ReadAt(FileStream fs, long off, int len)
    {
        var d = new byte[len]; fs.Position = off;
        int n = 0; while (n < len) { int r = fs.Read(d, n, len - n); if (r <= 0) throw new Exception("resS ends before the texture"); n += r; }
        return d;
    }

    internal static void WriteAt(FileStream fs, long off, byte[] d) { fs.Position = off; fs.Write(d, 0, d.Length); fs.Flush(); }

    // straight (non-premultiplied) RGBA, top-down
    static byte[] LoadPng(string path)
    {
        using (var bmp = new Bitmap(path))
        {
            if (bmp.Width != W || bmp.Height != H) throw new Exception(path + " is " + bmp.Width + "x" + bmp.Height + ", expected " + W + "x" + H);
            var d = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var res = new byte[W * H * 4];
                var row = new byte[W * 4];
                for (int y = 0; y < H; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(d.Scan0 + y * d.Stride, row, 0, row.Length);
                    for (int x = 0; x < W; x++)
                    {
                        int i = (y * W + x) * 4;
                        res[i] = row[x * 4 + 2]; res[i + 1] = row[x * 4 + 1]; res[i + 2] = row[x * 4]; res[i + 3] = row[x * 4 + 3];
                    }
                }
                return res;
            }
            finally { bmp.UnlockBits(d); }
        }
    }

    // Texture2D of Unity 6 without type trees: m_Name, 2 bools (aligned), width, height, complete size, mips stripped, format, mip count ...;
    // the object ends with StreamingInfo (u64 offset, u32 size, path string)
    internal static Tex ParseTexture(byte[] b, int start, int size)
    {
        int p = start;
        int nameLen = BitConverter.ToInt32(b, p); p += 4;
        if (nameLen < 0 || nameLen > 256) throw new Exception("unexpected Texture2D layout");
        var t = new Tex { Name = Encoding.UTF8.GetString(b, p, nameLen) };
        p += (nameLen + 3) & ~3;
        p += 4;
        t.Width = BitConverter.ToInt32(b, p); t.Height = BitConverter.ToInt32(b, p + 4);
        t.Format = BitConverter.ToInt32(b, p + 16);
        int end = start + size;
        // the path string is the last field: find its length by trying the padded sizes
        for (int len = 1; len < 256; len++)
        {
            int padded = (len + 3) & ~3;
            int lp = end - padded - 4;
            if (lp - 12 < start) break;
            if (BitConverter.ToInt32(b, lp) == len && Encoding.ASCII.GetString(b, lp + 4, len).EndsWith("resS"))
            {
                t.StreamPath = Encoding.ASCII.GetString(b, lp + 4, len);
                t.Size = BitConverter.ToUInt32(b, lp - 4);
                t.Offset = BitConverter.ToInt64(b, lp - 12);
                return t;
            }
        }
        t.StreamPath = "";
        return t;
    }
}

// BC3 / DXT5: 16 bytes per 4x4 block = 8 bytes alpha (two endpoints + 16 3-bit indices) + 8 bytes colour (two RGB565 endpoints + 16 2-bit indices)
static class Dxt5
{
    static int Expand5(int v) => (v << 3) | (v >> 2);
    static int Expand6(int v) => (v << 2) | (v >> 4);
    static int Pack(int r, int g, int bl) => ((r >> 3) << 11) | ((g >> 2) << 5) | (bl >> 3);

    static int[] AlphaPalette(int a0, int a1)
    {
        var p = new int[8]; p[0] = a0; p[1] = a1;
        if (a0 > a1) for (int i = 1; i <= 6; i++) p[1 + i] = ((7 - i) * a0 + i * a1) / 7;
        else { for (int i = 1; i <= 4; i++) p[1 + i] = ((5 - i) * a0 + i * a1) / 5; p[6] = 0; p[7] = 255; }
        return p;
    }

    static int[][] ColorPalette(int c0, int c1)
    {
        int[] Rgb(int c) => new[] { Expand5(c >> 11), Expand6((c >> 5) & 63), Expand5(c & 31) };
        var a = Rgb(c0); var b = Rgb(c1);
        var p = new int[4][]; p[0] = a; p[1] = b;
        if (c0 > c1) { p[2] = new[] { (2 * a[0] + b[0]) / 3, (2 * a[1] + b[1]) / 3, (2 * a[2] + b[2]) / 3 }; p[3] = new[] { (a[0] + 2 * b[0]) / 3, (a[1] + 2 * b[1]) / 3, (a[2] + 2 * b[2]) / 3 }; }
        else { p[2] = new[] { (a[0] + b[0]) / 2, (a[1] + b[1]) / 2, (a[2] + b[2]) / 2 }; p[3] = new[] { 0, 0, 0 }; }
        return p;
    }

    // 16 RGBA pixels, row 0 first (row 0 = the lowest row of the block in Unity's bottom-up layout)
    public static byte[] Decode(byte[] d, int o)
    {
        var px = new byte[64];
        var ap = AlphaPalette(d[o], d[o + 1]);
        long ai = 0; for (int i = 0; i < 6; i++) ai |= (long)d[o + 2 + i] << (8 * i);
        var cp = ColorPalette(d[o + 8] | d[o + 9] << 8, d[o + 10] | d[o + 11] << 8);
        uint ci = BitConverter.ToUInt32(d, o + 12);
        for (int i = 0; i < 16; i++)
        {
            var c = cp[(ci >> (2 * i)) & 3];
            px[i * 4] = (byte)c[0]; px[i * 4 + 1] = (byte)c[1]; px[i * 4 + 2] = (byte)c[2];
            px[i * 4 + 3] = (byte)ap[(ai >> (3 * i)) & 7];
        }
        return px;
    }

    public static void Encode(byte[] px, byte[] dst, int o)
    {
        EncodeAlpha(px, dst, o);
        EncodeColor(px, dst, o + 8);
    }

    static void EncodeAlpha(byte[] px, byte[] dst, int o)
    {
        int min = 255, max = 0, lo = 255, hi = 0;
        for (int i = 0; i < 16; i++)
        {
            int a = px[i * 4 + 3];
            min = Math.Min(min, a); max = Math.Max(max, a);
            if (a != 0 && a != 255) { lo = Math.Min(lo, a); hi = Math.Max(hi, a); }
        }
        // 8-value mode (a0 > a1) or 6-value mode with exact 0 and 255 (a0 <= a1): whichever is closer
        long bestErr = long.MaxValue; int bA0 = max, bA1 = min; long bIdx = 0;
        var tries = new List<int[]> { new[] { max, min } };
        if (hi >= lo) tries.Add(new[] { lo, hi }); else tries.Add(new[] { 0, 255 });
        foreach (var t in tries)
        {
            var pal = AlphaPalette(t[0], t[1]);
            long err = 0, idx = 0;
            for (int i = 0; i < 16; i++)
            {
                int best = 0, be = int.MaxValue;
                for (int k = 0; k < 8; k++) { int e = Math.Abs(pal[k] - px[i * 4 + 3]); if (e < be) { be = e; best = k; } }
                err += (long)be * be; idx |= (long)best << (3 * i);
            }
            if (err < bestErr) { bestErr = err; bA0 = t[0]; bA1 = t[1]; bIdx = idx; }
        }
        dst[o] = (byte)bA0; dst[o + 1] = (byte)bA1;
        for (int i = 0; i < 6; i++) dst[o + 2 + i] = (byte)(bIdx >> (8 * i));
    }

    static void EncodeColor(byte[] px, byte[] dst, int o)
    {
        // fit on the visible pixels only: the colour of a fully transparent pixel must not pull the endpoints
        var use = new List<int>();
        for (int i = 0; i < 16; i++) if (px[i * 4 + 3] > 0) use.Add(i);
        if (use.Count == 0) for (int i = 0; i < 16; i++) use.Add(i);

        var mean = new double[3];
        foreach (int i in use) for (int c = 0; c < 3; c++) mean[c] += px[i * 4 + c];
        for (int c = 0; c < 3; c++) mean[c] /= use.Count;
        var cov = new double[3, 3];
        foreach (int i in use) for (int a = 0; a < 3; a++) for (int b = 0; b < 3; b++) cov[a, b] += (px[i * 4 + a] - mean[a]) * (px[i * 4 + b] - mean[b]);
        var axis = new double[] { 0.57735, 0.57735, 0.57735 };
        for (int it = 0; it < 8; it++)
        {
            var n = new double[3];
            for (int a = 0; a < 3; a++) for (int b = 0; b < 3; b++) n[a] += cov[a, b] * axis[b];
            double len = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
            if (len < 1e-9) break;
            for (int a = 0; a < 3; a++) axis[a] = n[a] / len;
        }
        double tmin = double.MaxValue, tmax = double.MinValue;
        foreach (int i in use)
        {
            double t = 0; for (int c = 0; c < 3; c++) t += (px[i * 4 + c] - mean[c]) * axis[c];
            tmin = Math.Min(tmin, t); tmax = Math.Max(tmax, t);
        }
        int[] Pt(double t) { var r = new int[3]; for (int c = 0; c < 3; c++) r[c] = (int)Math.Max(0, Math.Min(255, Math.Round(mean[c] + axis[c] * t))); return r; }
        var e0 = Pt(tmax); var e1 = Pt(tmin);

        // assign, then one least-squares refinement of the endpoints from the assignment; keep the better of the two
        int bestC0 = 0, bestC1 = 0; uint bestIdx = 0; long bestErr = long.MaxValue;
        for (int round = 0; round < 2; round++)
        {
            int c0 = Pack(e0[0], e0[1], e0[2]), c1 = Pack(e1[0], e1[1], e1[2]);
            if (c0 < c1) { int t = c0; c0 = c1; c1 = t; }
            if (c0 == c1 && c0 < 0xFFFF) c0++;       // c0 > c1 keeps the 4-colour mode
            var pal = ColorPalette(c0, c1);
            uint idx = 0; long err = 0;
            var sel = new int[16];
            for (int i = 0; i < 16; i++)
            {
                int best = 0; long be = long.MaxValue;
                for (int k = 0; k < 4; k++)
                {
                    long e = 0; for (int c = 0; c < 3; c++) { int d = pal[k][c] - px[i * 4 + c]; e += d * d; }
                    if (e < be) { be = e; best = k; }
                }
                sel[i] = best; idx |= (uint)best << (2 * i);
                if (px[i * 4 + 3] > 0 || use.Count == 16) err += be;
            }
            if (err < bestErr) { bestErr = err; bestC0 = c0; bestC1 = c1; bestIdx = idx; }
            else break;
            // t of palette index: 0 -> c0 (t=0), 2 -> 1/3, 3 -> 2/3, 1 -> c1 (t=1)
            double s00 = 0, s01 = 0, s11 = 0; var r0 = new double[3]; var r1 = new double[3];
            foreach (int i in use)
            {
                double t = sel[i] == 0 ? 0 : sel[i] == 2 ? 1.0 / 3 : sel[i] == 3 ? 2.0 / 3 : 1;
                double w0 = 1 - t, w1 = t;
                s00 += w0 * w0; s01 += w0 * w1; s11 += w1 * w1;
                for (int c = 0; c < 3; c++) { r0[c] += w0 * px[i * 4 + c]; r1[c] += w1 * px[i * 4 + c]; }
            }
            double det = s00 * s11 - s01 * s01;
            if (Math.Abs(det) < 1e-6) break;
            e0 = new int[3]; e1 = new int[3];
            for (int c = 0; c < 3; c++)
            {
                e0[c] = (int)Math.Max(0, Math.Min(255, Math.Round((r0[c] * s11 - r1[c] * s01) / det)));
                e1[c] = (int)Math.Max(0, Math.Min(255, Math.Round((r1[c] * s00 - r0[c] * s01) / det)));
            }
        }
        dst[o] = (byte)bestC0; dst[o + 1] = (byte)(bestC0 >> 8); dst[o + 2] = (byte)bestC1; dst[o + 3] = (byte)(bestC1 >> 8);
        for (int i = 0; i < 4; i++) dst[o + 4 + i] = (byte)(bestIdx >> (8 * i));
    }
}
