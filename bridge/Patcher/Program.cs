using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Usage: Patcher <ManagedDir>
// Always patches from <name>.dll.orig -> <name>.dll so it is idempotent.
// Patches applied to Core.dll:
//   1. Bootstrap.Awake: replace call Coroutine_GameStartup() with ForgeBridge.LocalStartup.Run(Bootstrap)
var managed = args[0];
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(managed);

var bridge = ModuleDefinition.ReadModule(Path.Combine(managed, "ForgeBridge.dll"));
var bridgeRun = bridge.GetType("ForgeBridge.LocalStartup").Methods.Single(m => m.Name == "Run");

var corePath = Path.Combine(managed, "Core.dll");
var coreOrig = corePath + ".orig";
if (!File.Exists(coreOrig)) File.Copy(corePath, coreOrig);

var core = ModuleDefinition.ReadModule(coreOrig, new ReaderParameters { AssemblyResolver = resolver });

var bootstrap = core.GetType("Bootstrap");
var awake = bootstrap.Methods.Single(m => m.Name == "Awake");
var il = awake.Body.GetILProcessor();
var target = awake.Body.Instructions.Single(i =>
    i.OpCode == OpCodes.Call && i.Operand is MethodReference mr && mr.Name == "Coroutine_GameStartup");
target.Operand = core.ImportReference(bridgeRun);
Console.WriteLine("patched Bootstrap.Awake -> " + bridgeRun.FullName);

core.Write(corePath);
Console.WriteLine("wrote " + corePath);
