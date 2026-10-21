// Dumps every protobuf FileDescriptor embedded in an assembly (<X>Reflection.Descriptor) into a FileDescriptorSet.
// Usage: descdump <assembly.dll> <out.desc>
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.Reflection;

var managed = Path.GetDirectoryName(Path.GetFullPath(args[0]));
AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
{
    var p = Path.Combine(managed, new AssemblyName(e.Name).Name + ".dll");
    return File.Exists(p) ? Assembly.LoadFrom(p) : null;
};
var asm = Assembly.LoadFrom(args[0]);
Type[] types;
try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
var seen = new System.Collections.Generic.Dictionary<string, FileDescriptor>();
void Add(FileDescriptor fd)
{
    if (fd == null || seen.ContainsKey(fd.Name)) return;
    foreach (var dep in fd.Dependencies) Add(dep);
    seen[fd.Name] = fd;
}
foreach (var t in types)
{
    var prop = t.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static);
    if (prop == null || prop.PropertyType != typeof(FileDescriptor)) continue;
    try { Add((FileDescriptor)prop.GetValue(null)); } catch (Exception e) { Console.Error.WriteLine(t + ": " + e.Message); }
}
using var outStream = File.Create(args[1]);
var cos = new CodedOutputStream(outStream);
foreach (var fd in seen.Values)
{
    var bytes = fd.SerializedData;
    cos.WriteTag(1, WireFormat.WireType.LengthDelimited); // FileDescriptorSet.file
    cos.WriteBytes(bytes);
}
cos.Flush();
Console.WriteLine("files: " + seen.Count);
