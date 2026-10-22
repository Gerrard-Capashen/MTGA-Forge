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
//   4. InitConfig.GetBootstrapConfig: no BI tracker, Backtrace, Steam, Unity Services
//   5. ManifestMetadataProvider(): no manifest pointer request to Wizards' CDN
//   6. AssetLoader: art of generated cards (art id 9,000,000+) from ForgeBridge.CardArt
// and to AK.Wwise.Unity.API.dll:
//   7. AkUnitySoundEngine.InitCommunication: no Wwise profiler socket
if (args.Length > 1 && (args[0] == "identity" || args[0] == "unityconnect" || args[0] == "check-bridge" || args[0] == "audit-bridge")) return Tools.Run(args);
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

//   4. InitConfig.GetBootstrapConfig: no BI event tracker, Backtrace, Steam or Unity Services (file system utils and the
//      rider prompt replacer stay). Bootstrap.Awake builds the application from it before our hook runs.
var initConfig = core.GetType("Wotc.Mtga.Client.InitConfig");
var getBootstrapConfig = initConfig.Methods.Single(m => m.Name == "GetBootstrapConfig");
var initCtor = initConfig.Methods.Single(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 6);
var gb = getBootstrapConfig.Body;
gb.Instructions.Clear();
gb.Variables.Clear();
gb.ExceptionHandlers.Clear();
var gp = gb.GetILProcessor();
foreach (var on in new[] { true, true, false, false, false, false }) gp.Append(gp.Create(on ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0));
gp.Append(gp.Create(OpCodes.Newobj, initCtor));
gp.Append(gp.Create(OpCodes.Ret));
Console.WriteLine("patched InitConfig.GetBootstrapConfig -> BI tracker, Backtrace, Steam, Unity Services off");

//   5. ManifestMetadataProvider(): drop the PointerFileMetadataCollector (GET of a manifest pointer from Wizards' CDN);
//      the conf-file and doorbell collectors (local manifests) stay.
var mmpCtor = core.GetType("Core.Code.AssetBundles.Manifest.ManifestMetadataProvider").Methods.Single(m => m.IsConstructor && !m.IsStatic);
var pointerNew = mmpCtor.Body.Instructions.Single(i =>
    i.OpCode == OpCodes.Newobj && i.Operand is MethodReference ctor && ctor.DeclaringType.Name == "PointerFileMetadataCollector");
var pointerLoad = pointerNew.Previous;   // ldfld _collectors (after ldarg.0)
var pointerAdd = pointerNew.Next;        // callvirt List.Add
if (pointerLoad.OpCode != OpCodes.Ldfld || pointerLoad.Previous.OpCode != OpCodes.Ldarg_0 || pointerAdd.OpCode != OpCodes.Callvirt)
    throw new InvalidOperationException("ManifestMetadataProvider ctor changed shape");
foreach (var ins in new[] { pointerLoad.Previous, pointerLoad, pointerNew, pointerAdd }) { ins.OpCode = OpCodes.Nop; ins.Operand = null; }
Console.WriteLine("patched ManifestMetadataProvider() -> no manifest pointer request");

//   6. AssetLoader: art paths of generated cards (art id 9,000,000+) come from ForgeBridge.CardArt, not bundles:
//      HaveAsset/AddReferenceCount -> true, ReleaseAsset -> nothing, AcquireAsset<T> -> CardArt.Acquire(path) as T.
var cardArt = bridge.GetType("ForgeBridge.CardArt");
var owns = core.ImportReference(cardArt.Methods.Single(m => m.Name == "Owns"));
var acquire = core.ImportReference(cardArt.Methods.Single(m => m.Name == "Acquire"));
var assetLoader = core.GetType("AssetLoader");
MethodDefinition Static(string name) => assetLoader.Methods.Single(m => m.Name == name && m.IsStatic && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.String");
foreach (var (name, result) in new[] { ("HaveAsset", (bool?)true), ("AddReferenceCount", true), ("ReleaseAsset", null) })
{
    var m = Static(name);
    var mp = m.Body.GetILProcessor();
    var orig = m.Body.Instructions[0];
    mp.InsertBefore(orig, mp.Create(OpCodes.Ldarg_0));
    mp.InsertBefore(orig, mp.Create(OpCodes.Call, owns));
    mp.InsertBefore(orig, mp.Create(OpCodes.Brfalse, orig));
    if (result.HasValue) mp.InsertBefore(orig, mp.Create(result.Value ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0));
    mp.InsertBefore(orig, mp.Create(OpCodes.Ret));
    Console.WriteLine("patched AssetLoader." + name + " -> ForgeBridge.CardArt.Owns");
}
{
    var m = Static("AcquireAsset");
    var t = m.GenericParameters.Single();
    var mp = m.Body.GetILProcessor();
    var orig = m.Body.Instructions[0];
    var pop = mp.Create(OpCodes.Pop);
    mp.InsertBefore(orig, mp.Create(OpCodes.Ldarg_0));
    mp.InsertBefore(orig, mp.Create(OpCodes.Call, acquire));
    mp.InsertBefore(orig, mp.Create(OpCodes.Isinst, t));
    mp.InsertBefore(orig, mp.Create(OpCodes.Unbox_Any, t));
    mp.InsertBefore(orig, mp.Create(OpCodes.Dup));
    mp.InsertBefore(orig, mp.Create(OpCodes.Brfalse, pop));
    mp.InsertBefore(orig, mp.Create(OpCodes.Ret));
    mp.InsertBefore(orig, pop);
    Console.WriteLine("patched AssetLoader.AcquireAsset<T> -> ForgeBridge.CardArt.Acquire");
}

//   6b. ClientEventDefinitionList.GetBillboardImagePath/GetBladeImagePath/GetBackgroundImagePath(assetLookupSystem, EventContext):
//      events without art in the client (quick drafts) get "forge-event-art/<SET>.jpg" (CardArt.EventArtPath), served by the art server.
var eventArtPath = core.ImportReference(cardArt.Methods.Single(m => m.Name == "EventArtPath"));
var eventBackgroundPath = core.ImportReference(cardArt.Methods.Single(m => m.Name == "EventBackgroundPath"));
var eventDefs = core.GetType("ClientEventDefinitionList");
foreach (var name in new[] { "GetBillboardImagePath", "GetBladeImagePath", "GetBackgroundImagePath" })
{
    var m = eventDefs.Methods.Single(x => x.Name == name && x.IsStatic && x.Parameters.Count == 2 && x.Parameters[1].ParameterType.FullName == "Wizards.MDN.EventContext");
    var mp = m.Body.GetILProcessor();
    foreach (var ret in m.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
    {
        // reuse the ret instruction itself so branches that target it run the hook too
        ret.OpCode = OpCodes.Ldarg_1;
        var call = mp.Create(OpCodes.Call, name == "GetBackgroundImagePath" ? eventBackgroundPath : eventArtPath);
        mp.InsertAfter(ret, call);
        mp.InsertAfter(call, mp.Create(OpCodes.Ret));
    }
    Console.WriteLine("patched ClientEventDefinitionList." + name + "(EventContext) -> ForgeBridge.CardArt.EventArtPath");
}

core.Write(corePath);
Console.WriteLine("wrote " + corePath);

// Patches applied to AK.Wwise.Unity.API.dll:
//   7. AkUnitySoundEngine.InitCommunication: returns AK_Success without starting Wwise's profiler link (a UDP socket
//      on every interface, discovery port 24024, for the Wwise authoring tool). Its only caller ignores the result.
var akPath = Path.Combine(managed, "AK.Wwise.Unity.API.dll");
var akOrig = akPath + ".orig";
if (!File.Exists(akOrig)) File.Copy(akPath, akOrig);
var ak = ModuleDefinition.ReadModule(akOrig, new ReaderParameters { AssemblyResolver = resolver });
var initComm = ak.GetType("AkUnitySoundEngine").Methods.Single(m => m.Name == "InitCommunication" && m.Parameters.Count == 1);
var akSuccess = (int)ak.GetType("AKRESULT").Fields.Single(f => f.Name == "AK_Success").Constant;
initComm.Body.Instructions.Clear();
initComm.Body.Variables.Clear();
var ic = initComm.Body.GetILProcessor();
ic.Append(ic.Create(OpCodes.Ldc_I4, akSuccess));
ic.Append(ic.Create(OpCodes.Ret));
Console.WriteLine("patched AkUnitySoundEngine.InitCommunication -> no Wwise comms socket");
ak.Write(akPath);
Console.WriteLine("wrote " + akPath);
return 0;
