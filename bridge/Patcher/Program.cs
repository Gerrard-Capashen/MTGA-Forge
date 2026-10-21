using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Usage: Patcher <ManagedDir>
// Always patches from <name>.dll.orig -> <name>.dll so it is idempotent.
// Patches applied to Core.dll:
//   1. Bootstrap.Awake: replace call Coroutine_GameStartup() with ForgeBridge.LocalStartup.Run(Bootstrap)
//   2. WrapperDeckUtilities.TryImportDeck: rewrite the import text first (ForgeBridge.DeckImport)
//   3. DeckFormatUtil.GetBestFormat: imported decks get the narrowest Forge-defined format (ForgeBridge.DeckFormats)
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

//   2. WrapperDeckUtilities.TryImportDeck(importString, cardDatabase, ...): first
//      importString = ForgeBridge.DeckImport.Rewrite(importString, cardDatabase)   (loose name matching, skip unknown cards)
var rewrite = bridge.GetType("ForgeBridge.DeckImport").Methods.Single(m => m.Name == "Rewrite");
var tryImport = core.GetType("WrapperDeckUtilities").Methods.Single(m => m.Name == "TryImportDeck");
var ip = tryImport.Body.GetILProcessor();
var first = tryImport.Body.Instructions[0];
ip.InsertBefore(first, ip.Create(OpCodes.Ldarg_0));
ip.InsertBefore(first, ip.Create(OpCodes.Ldarg_1));
ip.InsertBefore(first, ip.Create(OpCodes.Call, core.ImportReference(rewrite)));
ip.InsertBefore(first, ip.Create(OpCodes.Starg, tryImport.Parameters[0]));
Console.WriteLine("patched WrapperDeckUtilities.TryImportDeck -> " + rewrite.FullName);

//   3. DeckFormatUtil.GetBestFormat(deck, formatManager, cardDb): every "return x" becomes
//      "return ForgeBridge.DeckFormats.Best(x, deck, formatManager, cardDb)". The ret itself turns into the first added
//      instruction, so branches that target it still run the call.
var best = bridge.GetType("ForgeBridge.DeckFormats").Methods.Single(m => m.Name == "Best");
var getBest = core.GetType("Wizards.MDN.DeckManager.DeckFormatUtil").Methods.Single(m => m.Name == "GetBestFormat");
var bp = getBest.Body.GetILProcessor();
foreach (var ret in getBest.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
{
    ret.OpCode = OpCodes.Ldarg_0;
    ret.Operand = null;
    var tail = new[] { bp.Create(OpCodes.Ldarg_1), bp.Create(OpCodes.Ldarg_2), bp.Create(OpCodes.Call, core.ImportReference(best)), bp.Create(OpCodes.Ret) };
    var after = ret;
    foreach (var ins in tail) { bp.InsertAfter(after, ins); after = ins; }
}
Console.WriteLine("patched DeckFormatUtil.GetBestFormat -> " + best.FullName);

core.Write(corePath);
Console.WriteLine("wrote " + corePath);
