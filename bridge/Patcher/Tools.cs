using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Mono.Cecil;

// Extra verbs of the Patcher (replace the Python scripts of the first patcher so a user machine needs no Python):
//   Patcher identity     <globalgamemanagers> [product]   company/product "Wizards Of The Coast"/"MTGA" -> "Forge Bridge Project"/product (4 characters, default "MTGF")
//   Patcher unityconnect <globalgamemanagers>   Unity analytics / cloud services off (UnityConnectSettings)
//   Patcher bootlogo    <MTGA_Data> <logo.png>   the Arena logo on the boot/login screen (both the default and the set-promo one) -> our 419x173 logo (BootLogo.cs)
//   Patcher check-bridge <ManagedDir>           every Core/Unity member ForgeBridge.dll references still resolves in ManagedDir
//   Patcher audit-bridge <ForgeBridge.dll>      the dll defines only our own namespace and embeds no resources (nothing copied from the game)
static class Tools
{
    public static int Run(string[] args)
    {
        try
        {
            switch (args[0])
            {
                case "identity": Identity(args[1], args.Length > 2 ? args[2] : "MTGF"); return 0;
                case "unityconnect": UnityConnect(args[1]); return 0;
                case "bootlogo": BootLogo.Apply(args[1], args[2]); return 0;
                case "check-bridge": return CheckBridge(args[1]);
                case "audit-bridge": return AuditBridge(args[1]);
            }
        }
        catch (Exception e) { Console.Error.WriteLine(args[0] + " failed: " + e.Message); return 1; }
        Console.Error.WriteLine("unknown verb " + args[0]);
        return 2;
    }

    static int Find(byte[] b, byte[] pat, int from = 0)
    {
        for (int i = from; i <= b.Length - pat.Length; i++)
        {
            int j = 0;
            while (j < pat.Length && b[i + j] == pat[j]) j++;
            if (j == pat.Length) return i;
        }
        return -1;
    }

    static int Count(byte[] b, byte[] pat) { int n = 0, i = -1; while ((i = Find(b, pat, i + 1)) >= 0) n++; return n; }

    static void BackupOnce(string path) { if (!File.Exists(path + ".orig")) File.Copy(path, path + ".orig"); }

    // Unity takes the LocalLow log directory and the registry key from these (same length: no offset moves)
    // product must be 4 characters (several clients on one machine use "MTGF", "MTG2", ...: own registry key and LocalLow folder)
    static void Identity(string path, string product)
    {
        if (product.Length != 4) throw new Exception("product must be 4 characters: " + product);
        byte[] Pat(string company, string prod) => new byte[] { 0x14, 0, 0, 0 }.Concat(Encoding.ASCII.GetBytes(company))
            .Concat(new byte[] { 4, 0, 0, 0 }).Concat(Encoding.ASCII.GetBytes(prod)).ToArray();
        var nw = Pat("Forge Bridge Project", product);
        var b = File.ReadAllBytes(path);
        if (Count(b, nw) == 1) { Console.WriteLine("already patched"); return; }
        // the original, or a client patched with another product name
        var old = Pat("Wizards Of The Coast", "MTGA");
        int n = Count(b, old);
        if (n == 0)
        {
            // keep the prefix, replace the 4 product bytes
            var prefix = Pat("Forge Bridge Project", "");
            int pi = Find(b, prefix);
            if (pi < 0 || Find(b, prefix, pi + 1) >= 0) throw new Exception("expected exactly one identity occurrence, found none or several");
            BackupOnce(path);
            Array.Copy(Encoding.ASCII.GetBytes(product), 0, b, pi + prefix.Length, 4);
            File.WriteAllBytes(path, b);
            Console.WriteLine("product changed at offset " + pi);
            return;
        }
        if (n != 1) throw new Exception("expected exactly one identity occurrence, found " + n);
        BackupOnce(path);
        int i = Find(b, old);
        Array.Copy(nw, 0, b, i, nw.Length);
        File.WriteAllBytes(path, b);
        Console.WriteLine("patched at offset " + i);
    }

    // (class id, data start, size) of every object; SerializedFile format 22 without type trees
    internal static List<(int cid, long start, uint size, long id)> Objects(byte[] b)
    {
        uint Be32(int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
        var version = Be32(8);
        if (version != 22) throw new Exception("unsupported SerializedFile version " + version);
        long dataOffset = ((long)Be32(32) << 32) | Be32(36);
        if (b[40] != 0) throw new Exception("expected little-endian metadata");
        int p = Array.IndexOf(b, (byte)0, 48) + 1 + 4;
        if (b[p] != 0) throw new Exception("file has type trees: only the stripped layout is handled");
        p++;
        int count = BitConverter.ToInt32(b, p); p += 4;
        var classes = new List<int>();
        for (int i = 0; i < count; i++)
        {
            int cid = BitConverter.ToInt32(b, p);
            p += 7 + (cid == 114 ? 16 : 0) + 16;
            classes.Add(cid);
        }
        int n = BitConverter.ToInt32(b, p); p += 4;
        p = (p + 3) & ~3;
        var res = new List<(int, long, uint, long)>();
        for (int i = 0; i < n; i++)
        {
            long id = BitConverter.ToInt64(b, p);
            long start = BitConverter.ToInt64(b, p + 8);
            uint size = BitConverter.ToUInt32(b, p + 16);
            int type = BitConverter.ToInt32(b, p + 20);
            p += 24;
            res.Add((classes[type], dataOffset + start, size, id));
        }
        return res;
    }

    static void UnityConnect(string path)
    {
        const int UnityConnectSettings = 310;
        var dead = "http://127.0.0.1:9/";
        var b = File.ReadAllBytes(path);
        var found = Objects(b).Where(o => o.cid == UnityConnectSettings).ToList();
        if (found.Count != 1) throw new Exception("expected one UnityConnectSettings object, found " + found.Count);
        int start = (int)found[0].start, size = (int)found[0].size;
        var body = new byte[size];
        Array.Copy(b, start, body, 0, size);
        if (!((body[0] == 0 || body[0] == 1) && body[2] == 0 && body[3] == 0)) throw new Exception("unexpected UnityConnectSettings header");

        var text = Encoding.GetEncoding(28591).GetString(body);   // latin1: char index == byte index
        var urls = new List<(int off, int len, string url)>();
        foreach (Match m in Regex.Matches(text, @"https?://[\x21-\x7e]+"))
        {
            if (m.Index < 4) continue;
            int len = BitConverter.ToInt32(body, m.Index - 4);
            if (len == m.Length) urls.Add((m.Index, len, m.Value));   // a whole serialized string, not text inside one
        }
        if (urls.Count == 0) throw new Exception("no URL strings in UnityConnectSettings");

        bool changed = body[0] != 0 || urls.Any(u => !u.url.StartsWith(dead));
        if (!changed) { Console.WriteLine("already patched (" + urls.Count + " URLs dead, m_Enabled=0)"); return; }
        BackupOnce(path);
        b[start] = 0;
        foreach (var (off, len, url) in urls)
        {
            var repl = Encoding.ASCII.GetBytes((dead + new string('x', len)).Substring(0, len));
            Array.Copy(repl, 0, b, start + off, len);
            if (!url.StartsWith(dead)) Console.WriteLine("   " + url + " -> " + Encoding.ASCII.GetString(repl));
        }
        File.WriteAllBytes(path, b);
        Console.WriteLine("patched UnityConnectSettings at 0x" + start.ToString("x") + ": m_Enabled=0, " + urls.Count + " URLs dead");
    }

    static int AuditBridge(string dll)
    {
        var m = ModuleDefinition.ReadModule(dll);
        var foreign = m.Types.Where(t => t.Name != "<Module>" && t.Namespace != "ForgeBridge" && !t.Name.StartsWith("<")
            && !t.CustomAttributes.Any(a => a.AttributeType.Name == "CompilerGeneratedAttribute" || a.AttributeType.Name == "EmbeddedAttribute")).ToList();
        foreach (var t in foreign) Console.Error.WriteLine("  type outside ForgeBridge: " + t.FullName);
        foreach (var r in m.Resources) Console.Error.WriteLine("  embedded resource: " + r.Name);
        Console.WriteLine("audit-bridge: " + m.Types.Count + " types, " + foreign.Count + " foreign, " + m.Resources.Count + " resources");
        return foreign.Count == 0 && m.Resources.Count == 0 ? 0 : 1;
    }

    // The prebuilt ForgeBridge.dll was compiled against another build. This is the compile check without a compiler:
    // every type, method and field of the game's own assemblies that it references must still exist.
    static int CheckBridge(string managed)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managed);
        var rp = new ReaderParameters { AssemblyResolver = resolver };
        var bridge = ModuleDefinition.ReadModule(Path.Combine(managed, "ForgeBridge.dll"), rp);
        var gameAsm = new HashSet<string>(Directory.GetFiles(managed, "*.dll").Select(Path.GetFileNameWithoutExtension));
        bool Game(IMetadataScope s)
        {
            var n = s is AssemblyNameReference a ? a.Name : null;
            return n != null && gameAsm.Contains(n) && n != "netstandard" && n != "mscorlib" && n != "ForgeBridge"
                && !n.StartsWith("System") && !n.StartsWith("Microsoft.") && !n.StartsWith("Mono.");
        }
        int ok = 0;
        var bad = new List<string>();
        foreach (var t in bridge.GetTypeReferences().Where(t => Game(t.Scope)))
        {
            if (t.Resolve() == null) bad.Add("type " + t.FullName); else ok++;
        }
        foreach (var m in bridge.GetMemberReferences().Where(m => m.DeclaringType != null && Game(m.DeclaringType.Scope)))
        {
            bool found = m is MethodReference mr ? mr.Resolve() != null : m is FieldReference fr ? fr.Resolve() != null : true;
            if (found) ok++; else bad.Add("member " + m.FullName);
        }
        foreach (var s in bad.Distinct()) Console.Error.WriteLine("  MISSING " + s);
        Console.WriteLine("check-bridge: " + ok + " references resolved, " + bad.Distinct().Count() + " missing");
        return bad.Count == 0 ? 0 : 1;
    }
}
