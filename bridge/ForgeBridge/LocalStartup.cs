using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Assets.Core.Code.Doorbell;
using Core.Shared.Code.Connection;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Wizards.Mtga;
using Wizards.Mtga.Assets;
using Wotc.Mtga.Client;
using Wotc.Mtga.Network.ServiceWrappers;

namespace ForgeBridge
{
    /// <summary>
    /// Replacement for Bootstrap.Coroutine_GameStartup. Runs the client in "local mode":
    /// no Doorbell, no Front Door, no Wizards account.
    /// Stage 1 (this file): PAPA + local account + synthetic doorbell (local manifests) + bundle provisioner.
    /// </summary>
    public static class LocalStartup
    {
        /// <summary>FORGEBRIDGE_MODE=duel: old path straight into a duel (scripted rig recipes). Default: menus via the local Front Door.</summary>
        public static readonly bool DuelMode = string.Equals(Environment.GetEnvironmentVariable("FORGEBRIDGE_MODE"), "duel", StringComparison.OrdinalIgnoreCase);
        public static readonly int FdPort = int.TryParse(Environment.GetEnvironmentVariable("FORGEBRIDGE_FDPORT"), out var fp) ? fp : 30010;

        private static T Field<T>(object o, string name)
        {
            var f = o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f == null) throw new MissingFieldException(o.GetType().Name, name);
            return (T)f.GetValue(o);
        }

        private static void SetField(object o, string name, object value)
        {
            var f = o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f == null) throw new MissingFieldException(o.GetType().Name, name);
            f.SetValue(o, value);
        }

        private static object Call(object o, string name, params object[] args)
        {
            var m = o.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (m == null) throw new MissingMethodException(o.GetType().Name, name);
            return m.Invoke(o, args);
        }

        public static IEnumerator Run(Bootstrap bootstrap)
        {
            Log.Info("LocalStartup.Run entered. Original startup disabled; no Wizards network calls.");
            Privacy.Apply();
            // a quit without an exception in Player.log: say who asked for it; held while others draft at this player's table
            Application.wantsToQuit += HostQuitGuard.WantsToQuit;
            bootstrap.StartCoroutine(HostQuitGuard.Watch());
            bootstrap.StartCoroutine(CardArt.Pump());
            bootstrap.StartCoroutine(CoordinatorSocialManager.Pump());
            bootstrap.StartCoroutine(CosmeticCatalogs.Watch());
            bootstrap.StartCoroutine(MasteryTweaks.Watch());
            bootstrap.StartCoroutine(RewardPopupIcons.Watch());
            bootstrap.StartCoroutine(DebugShots.Run());
            bootstrap.StartCoroutine(DebugClicks.Run());
            IEnumerator stage = Stages(bootstrap);
            while (true)
            {
                bool more;
                try { more = stage.MoveNext(); }
                catch (Exception e)
                {
                    Log.Error("Startup stage failed: " + e);
                    break;
                }
                if (!more) break;
                yield return stage.Current;
            }
            Log.Info("LocalStartup finished (or failed); idling.");
            while (true) yield return new WaitForSeconds(30f);
        }

        private static IEnumerator Stages(Bootstrap bootstrap)
        {
            Log.Info("stage1: registering LocalAccountClient");
            Pantry.Set<IAccountClient>(Pantry.Scope.Application, new LocalAccountClient());
            // Hasbro Go (social/chat SDK) cannot initialise offline: use the null implementations the client already ships.
            MTGA.Social.ISocialManager social = CoordinatorSocialManager.Enabled ? (MTGA.Social.ISocialManager)new CoordinatorSocialManager()
                : SpikeSocialManager.Enabled ? new SpikeSocialManager() : new Wizards.Mtga.Social.NullSocialManager();
            Pantry.Set<MTGA.Social.ISocialManager>(Pantry.Scope.Application, social);
            Pantry.Set<_3rdParty.ExternalChat.IExternalChatManager>(Pantry.Scope.Application, new _3rdParty.ExternalChat.MockExternalChatManager());

            // TLS trust must be in place before PAPA.Create: the Front Door TcpConnection captures the callback when it is built.
            LocalMatch.InstallCertificateTrust();
            Log.Info("stage1: PAPA.Create (mode=" + (DuelMode ? "duel" : "frontdoor") + ")");
            var arenaApp = Field<IArenaApplication>(bootstrap, "_arenaApplication");
            Log.Info("stage1: arenaApp=" + (arenaApp == null ? "null" : arenaApp.GetType().Name));
            PAPA papa;
            try { papa = PAPA.Create(arenaApp); }
            catch (Exception e) { Log.Error("PAPA.Create threw: " + e); throw; }
            Log.Info("stage1: PAPA created");
            SetField(bootstrap, "_papa", papa);

            var fdcm = Pantry.Get<FrontDoorConnectionManager>();
            // the client drops its Front Door after 20 min without input ("User Idle") and its reconnect only re-authenticates
            // (events and courses are not fetched again, so event pages break): pointless against a local server, so never
            fdcm.IdleTimeoutSec = double.MaxValue;
            if (!DuelMode)
            {
                var env = fdcm.CurrentEnvironment;
                Log.Info("stage1: environment '" + env?.name + "' fd " + env?.fdHost + ":" + env?.fdPort + " -> 127.0.0.1:" + FdPort);
                env.fdHost = LocalMatch.Host;
                env.fdPort = FdPort;
            }
            Log.Info("stage1: InitializeConnectionCommand");
            new InitializeConnectionCommand().Execute(papa.AssetLookupSystem, fdcm,
                Pantry.Get<IFrontDoorConnectionServiceWrapper>(), papa.MatchManager, papa.Matchmaking,
                papa.EventManager, papa.KeyBoardManager, papa.Actions, papa.SettingsMenuHost);
            var connectionManager = Pantry.Get<ConnectionManager>();
            SetField(bootstrap, "_connectionManager", connectionManager);

            Log.Info("stage1: synthetic doorbell from local manifests");
            connectionManager.DoorbellResponse = LocalDoorbell.Build("tcp://" + LocalMatch.Host + ":" + FdPort);

            Log.Info("stage1: AssetLoader.Initialize + bundle sources");
            AssetLoader.Initialize(papa.BILogger);
            var storage = Wizards.Mtga.Platforms.PlatformContext.GetStorageContext();
            SetField(bootstrap, "_embeddedPathResolver", storage.GetEmbeddedBundlePathResolver());
            Call(bootstrap, "PopulateAssetBundleSources");

            Log.Info("stage1 complete. Account=" + Pantry.Get<IAccountClient>().AccountInformation.PersonaID);
            // every card is unlocked (no collection, no vault): never show the draft's "Vault Progress" popup
            MDNPlayerPrefs.SetDraftHasSeenVaultPopup(Pantry.Get<IAccountClient>().AccountInformation.PersonaID, true);

            // ---- stage 2: bundles, localization, card database ----
            Log.Info("stage2: wwise listener + audio manager");
            UnityEngine.Object.Instantiate(Field<GameObject>(bootstrap, "_wwiseGlobalListener"));
            AudioManager.InitializeAudioManager(papa.AssetLookupSystem, storage);
            while (!AudioManager.Instance.Initialized) yield return null;
            Log.Info("stage2: audio manager ready (loadError=" + AudioManager.Instance.HadLoadError + ")");

            Log.Info("stage2: loading AssetPrep scene");
            var loadAssetPrep = AssetPrepScene.LoadAndInit();
            while (!loadAssetPrep.IsCompleted) yield return null;
            if (loadAssetPrep.IsFaulted) throw loadAssetPrep.Exception;
            var assetPrepScene = loadAssetPrep.Result;

            var provisioner = Field<AssetBundleProvisioner>(bootstrap, "_bundleProvisioner");
            var embedded = Field<object>(bootstrap, "_embeddedPathResolver");
            var cts = Field<System.Threading.CancellationTokenSource>(bootstrap, "clientLifetimeTokenSource");
            Log.Info("stage2: PrepareDownload (validates local bundles against local manifests; NO downloads)");
            var prepare = (IEnumerator)typeof(AssetPrepScene).GetMethod("Coroutine_AttemptWithRetries")
                .Invoke(assetPrepScene, new object[]
                {
                    (Func<System.Threading.Tasks.Task>)(() => provisioner.PrepareDownload((IAssetPathResolver)embedded, assetPrepScene, cts.Token)),
                    null
                });
            while (prepare.MoveNext()) yield return prepare.Current;
            Log.Info("stage2: PrepareDownload done. missing(General)=" + provisioner.CheckHasMissingBundlesInQueue(AssetPriority.General)
                + " missing(Future)=" + provisioner.CheckHasMissingBundlesInQueue(AssetPriority.Future)
                + " available=" + provisioner.AvailableBundles.Count);

            Log.Info("stage2: PrepareLoginAssets (AssetLoader + loc DB)");
            // client 2026.64 returned an IEnumerator; later builds do the work synchronously (void)
            var loginAssets = Call(bootstrap, "PrepareLoginAssets", assetPrepScene) as IEnumerator;
            if (loginAssets != null) while (loginAssets.MoveNext()) yield return loginAssets.Current;
            FormatLoc.Install();
            ChallengeSlots.Install();

            if (!DuelMode)
            {
                IEnumerator fd = FrontDoorStages(bootstrap, fdcm);
                while (fd.MoveNext()) yield return fd.Current;
                yield break;
            }

            Log.Info("stage2: card database");
            var cardDb = new Core.Shared.Code.ServiceFactories.LoadCardDatabaseUniTask().Load().ToCoroutine();
            while (cardDb.MoveNext()) yield return cardDb.Current;
            typeof(PAPA).GetProperty("CardDatabase").GetSetMethod(true).Invoke(papa, new object[] { Pantry.Get<Wotc.Mtga.Cards.Database.CardDatabase>() });
            papa.EventManager.Inject(papa.CardDatabase);
            papa.CreateFormatManager();
            papa.SetupDeckFormatFactory();
            papa.InitCardBuilders();
            Log.Info("stage2: card database loaded");

            AudioManager.InitializeAudio(papa.AssetLookupSystem);
            while (!AudioManager.Instance.Initialized) yield return null;
            papa.InitializeNpe();
            papa.InitializeMatchMaking();
            Log.Info("stage2 complete");

            // ---- stage 3: start a local match against the Match Door on loopback ----
            LocalMatch.InstallCertificateTrust();
            Log.Info("stage3: SetupTournamentMatch (loads duel scene)");
            papa.Matchmaking.SetupTournamentMatch();
            var cfg = LocalMatch.BuildConfig();
            Log.Info("stage3: delivering NewMatchCreatedConfig -> " + cfg.matchEndpointHost + ":" + cfg.matchEndpointPort + " match=" + cfg.matchId);
            Call(papa.Matchmaking, "OnMatchCreated", cfg);
            Log.Info("stage3 dispatched; duel scene should now connect");
            yield break;
        }
        /// <summary>
        /// Front Door mode: log in against the local Front Door (Java, same process as the Match Door), then run the
        /// client's own post-login sequence (assets, card DB, formats, active matches) which loads the home page.
        /// </summary>
        private static IEnumerator FrontDoorStages(Bootstrap bootstrap, FrontDoorConnectionManager fdcm)
        {
            Log.Info("fd: TryFastLogIn -> " + LocalMatch.Host + ":" + FdPort);
            var state = AutoLoginState.None;
            IEnumerator login = fdcm.TryFastLogIn(x => state = x);
            while (login.MoveNext()) yield return login.Current;
            Log.Info("fd: login state " + state);
            if (state != AutoLoginState.Connected)
            {
                Log.Error("fd: Front Door login failed (is the Java server running on port " + FdPort + "?)");
                yield break;
            }
            Log.Info("fd: starting Bootstrap.Coroutine_PostLogInSequence");
            bootstrap.StartCoroutine((IEnumerator)Call(bootstrap, "Coroutine_PostLogInSequence"));
        }
    }
}

