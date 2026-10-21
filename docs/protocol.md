# MTGA client protocol notes (extracted from decompiled client)

Source paths below refer to the decompiled output (see PROGRESS.md for how to regenerate).

## Schemas
`docs/proto/*.proto` + `gre.desc` are generated from the descriptors embedded in `Wizards.MDN.GreProtobuf.dll`
(`tools/desc2proto.py`; extractor is a tiny .NET console app that reads `<X>Reflection.Descriptor.SerializedData`).
- GREMessages.proto (181 msgs), GREMessagesCommon.proto (125 enums), GRETHHostMessages.proto (45),
  MatchServiceMessages.proto (17 msgs), ServiceMessagesCommon.proto (6).
- proto3, package `wotc.mtgo.gre.external.messaging`. Java: generate with the Maven protobuf plugin (grpc_tools lacks a java plugin).

## Boot sequence (Core/Bootstrap.cs `Coroutine_GameStartup`)
1. `PlatformContext.TryEstablishPlatformAuthentication` (Steam/Epic)
2. `CheckForUpdate`
3. **Doorbell**: HTTP POST to `EnvironmentDescription.doorbellUri` (+route) -> `{FdURI, BundleManifests}`
   (`Assets.Core.Code.Doorbell`). Override only honoured when `Debug.isDebugBuild`.
4. Asset bundle provisioning (downloads from manifests; already cached in `Downloads/`).
5. Login: `IAccountClient` (interface! `LogIn_Fast`, `LogIn_Credentials`, ...) -> `AccountInformation` (PersonaID, Credentials.Jwt, roles).
6. Front Door connect (`FrontDoorConnectionAWS`): same TCP framing as Match Door, TLS; commands are JSON keyed by `CmdType` (186 values, see below). First command `Authenticate`.
7. Post-login sequence, then main menu.

`EnvironmentDescription` fields: fdHost/fdPort, mdHost/mdPort, ecoUri, accountSystemBaseUri, doorbellUri, digitProfileBaseUri, sgsUri, bikeUri ... (all Wizards-hosted by default).

## Front Door
Implemented locally by `forge/forge-gui-mtga/src/main/java/forge/mtga/fd/` (FrontDoorServer + FrontDoor, 127.0.0.1:30010,
same JVM as the Match Door). Traced from SharedClientCore/FrontDoorConnectionAWS.cs, the `Aws*ServiceWrapper`s,
Bootstrap.cs and WrapperController.cs, and verified against the running client.

### Transport and envelope
- Same TCP/TLS framing as the Match Door (`FrameCodec`: version 4, type|format<<4, int32 LE length; Ping/Pong).
  TLS: the client's `TcpConnection` captures `ServicePointManager.ServerCertificateValidationCallback` when the FD
  connection object is built (inside `PAPA.Create`), so the bridge installs its CN=localhost trust BEFORE `PAPA.Create`.
- Client -> server: protobuf `Wizards.Arena.Protocol.Cmd{Type=CmdType, RawTransId=guid string,
  oneof{ProtobufPayload=Any(3), JsonPayload=bytes(4)}, Compressed}`.
  Server -> client: `Response{RawTransId, oneof{ProtobufPayload=Any(2), JsonPayload(3)}, Error=ServerError{code,message,data}(4), Compressed}`.
- Schemas: `docs/proto/fd.desc` / `docs/proto/fd/*.proto`, dumped from Wizards.Arena.Models.Protobuf.dll with
  `tools/descdump` (`dotnet run -c Release -- <Managed>\Wizards.Arena.Models.Protobuf.dll docs\proto\fd.desc`), merged per
  package for Java into `forge-gui-mtga/src/main/proto/Wizards.Arena.*.proto`
  (`python tools/desc2proto.py docs/proto/fd.desc <dir> com.wizards.mtga.fd merge`; one file per package keeps protoc's
  command line short on Windows; duplicate enum value names get an `Enum_` prefix, which is wire-compatible; CmdType
  values that clash with message names become `CmdType_X`).
- A reply must use the encoding of the request: JSON calls (`SendMessage<T>(cmd, object)`) are parsed with Newtonsoft
  (+ `FrontDoorConnectionContractResolver`), protobuf calls (`SendMessage<T>(cmd, IMessage)`) with `Any.Unpack<T>`.
  Proto CmdType names keep underscores (`Graph_GetGraphDefinitions`), the C# names drop them (`GraphGetGraphDefinitions`).
- Newtonsoft default contracts match member names case-insensitively, but the custom converters registered in the
  contract resolver (Deck, DeckCard, DTO_DeckSummaryV3, InventoryInfo, ClientMatchInfoV4, DTO_KillSwitchNotification,
  ClientPeriodicRewards, GetAllPreconDecksResponseV2, GetSkusAndListingsResponse, ...) are exact-name and do NOT skip the
  value of an unknown property: only send keys they read.
- Push notifications: a Response without a matching TransId whose JSON is a `PushNotification{Type: ENotificationType, ...}`,
  e.g. `{"Type":"MatchCreated","MatchInfoV4":{MatchEndpointHost, MatchEndpointPort, MatchId, McFabricId, EventId,
  Battlefield, MatchType:"Queue", YourSeat, ClientMetadata:{}, PlayerInfos:[{ScreenName, RankingClass, RankingTier, SeatId,
  TeamId, IsWotc}]}}`.
- Errors: `Response.Error{code,message}`; most wrappers treat it as "unsuccessful" and carry on.

### Boot / login (bridge LocalStartup, menu mode = default; FORGEBRIDGE_MODE=duel keeps the old direct-to-duel path)
1. Same stage 1/2 as duel mode (PAPA, local account, synthetic doorbell, bundles, loc DB); `CurrentEnvironment.fdHost/fdPort`
   are set to 127.0.0.1:30010 (what `FrontDoorConnectionManager` reads at login; FORGEBRIDGE_FDPORT overrides the port).
2. `FrontDoorConnectionManager.TryFastLogIn` -> `LocalAccountClient.LogIn_Fast` -> FD connect -> `Authenticate`
   `{ClientVersion, Token, PlatformId, ...}` -> JSON `FrontDoorSessionResp{SessionId, Attached:true, PlayerConditions:0}`.
   LocalAccountClient: credentials expire in 10 years (int.MaxValue overflowed into an always-expired token, so
   TokenManager kept showing "Connection Lost"), RefreshAccessToken succeeds, no MTGA_DEBUG role (it enables the
   client's debug log overlay; FORGEBRIDGE_DEBUGROLE=1 to get it back).
3. `Bootstrap.Coroutine_PostLogInSequence` (via reflection): waits `INpeStrategy.Initialized` (needs the NPE graph, below),
   PrepareAssets (`GetDesignerMetadata`, card DB, `GetFormats`, `GetSets`), `EventGetActiveMatches`, loads the wrapper
   (HomePageContext), `GetKillSwitchData`.
4. `WrapperController.Coroutine_StartupSequence`: `StartHook` (must succeed), `StaticContent`, then a batch:
   `CardGetAllCards`, `RankGetCombinedRankInfo`, `RankGetSeasonAndRankDetails`, `RenewalGetCurrentRenewal`,
   `StoreGetEntitlementsV2`, `DeckGetAllPreconDecksV3`, `GetPlayerPreferences`, `GetPlayBladeQueueConfig`,
   `GetNetDeckFolders`, `GetVoucherDefinitions`, `GetPlayerInbox`, `GetWpnNotificationPreferences`, `GetAllPrizeWalls`,
   `GetAllPreferredPrintings`, `ChallengeReconnectAll`; then `MercGetSkusAndListings`, `EventGetActiveEventsV2`,
   `EventGetCoursesV2`; the home page adds `QuestGetQuests`, `PeriodicRewardsGetStatus`, `RankEvaluatePayoutsV2`,
   `CarouselGetCarouselItems`, `CosmeticsSetPreferredAvatar`, `SetPlayerPreferences`, `LogBusinessEventsV2`.

### Command table (what the local Front Door answers)
| CmdType | enc | answer / notes |
|---|---|---|
| Authenticate, Attach | JSON | FrontDoorSessionResp{SessionId, Attached:true, PlayerConditions:0} |
| StartHook | proto | StartHookResponseV2: InventoryInfo (0 gold/gems), DeckLimit 1000, **DeckSummaries + DecksInternal (all decks)**, kill switches, empty cosmetics/achievements, ServerTimeInternal. The client builds its deck cache from this list and marks it clean; it only calls DeckGetDeckSummariesV3 after something marks it dirty, so the decks MUST be here. |
| CardGetAllCards | JSON | `{cacheVersion, cards:{grpId:4}}`: every primary non-token printing whose name Forge knows (20,121 of 20,159; the rest are Y25/Y26 digital cards). Titles: Localizations_enUS Formatted=0 else 1, tags stripped, split "A /// B" -> "A // B" |
| GetFormats | proto | `ForgeFormats`: Standard, Pioneer, Modern, Legacy, Vintage, Pauper, Commander, Historic (Forge `res/formats`), Alchemy + Timeless (every Forge card). Legality = exact `allowedTitleIds` (Forge `getFilterRules`) + `bannedTitleIds` + `individualCardQuotas` (Vintage restricted) + one unique placeholder `legalSets` entry per format (else the client's `IsSame` shares legal-title sets); Commander: main 99, `commandZoneQuota` 1-2, singleton, CompanionOnly. Groups ConstructedSortOrder + EvergreenFormats. Labels for Modern/Legacy/Vintage/Commander come from the bridge (`FormatLoc`) |
| GetSets | proto | SetMetadataResponse: one SetMetadata per expansion (Available, published, major, fake increasing ReleaseDate ordered by max GrpId, one collation named like the set), SetGroup **"AllFilters"** (required: KeyNotFound otherwise and boot hangs at "Requesting format data"), SetCollectionGroup set -> GrpIds (the profile's set-collection badge NREs without it and the Profile page hangs) |
| GetDesignerMetadata | JSON | empty lists |
| Graph_GetGraphDefinitions / GraphGetGraphState / GraphProcessV2 | JSON | three graphs, all nodes Completed: `NPE_Tutorial` (node in "Completed_Group" -> NodeNpeStrategy Initialized, tutorial complete), `ColorChallenge` (node `UnlockPlayMode` completed -> SparkyTourState.ClientForcedToUnlock: every play mode unlocked), `NewPlayerExperience` (milestones OpenDualColorPreconEvent, OpenSparkQueue, GraduateSparkRank, NPE_Completed) |
| GetKillSwitchData (+ StartHook) | JSON / proto | KillSwitches = UxKillSwitches = WPNFavoriteStoreStatus, PlayerReportingStatus, HasbroGoAddRemoveFriends, Social*Status, AchievementSceneStatus, TournamentStatus, BoosterStatus (true = feature off: no Wizards web calls; Achievements hidden, Packs locked) |
| GetPlayerPreferences / SetPlayerPreferences | JSON | string map persisted in `data/preferences.json` (play-blade selection etc.) |
| DeckGetDeckSummariesV3 / DeckGetDeck / DeckUpsertDeckV3 / DeckDeleteDeck | JSON | `data/decks/<id>.json` = {Summary, Deck} exactly as the client sent them in UpsertDeckReqV3; upsert returns the Summary, delete returns the raw string "Success" |
| GetPlayBladeQueueConfig | JSON | `[{Id:"forge-ai", QueueType:"Unranked", LocTitle:"Events/Event_Title_AIBotMatch", EventNameBO1:"AIBotMatch"}]` |
| EventGetActiveEventsV2 | JSON | `AiBotMatches:[AIBotMatch (Timeless: any deck), AIBotMatch_Rebalanced (Alchemy)]`; the client builds the bot events itself (AwsEventHelpers.ConvertToEventInfoV2) and only treats these two internal names as bot matches |
| EventGetCoursesV2 | JSON | `{Courses:[]}` |
| EventAiBotMatch | JSON | `{deckId, botDeckId, botMatchType}` -> Forge match set up (`forge.mtga.MatchSetup`; AI deck generated by Forge for the deck's `Format` attribute when it is a Forge 60-card format), reply = match id string, then MatchCreated push (1.5 s later) for 127.0.0.1:30003 |
| EventGetMatchResultReport | JSON | `{CurrentModule:"Complete", FoundMatch:true, questUpdates:[]}` (the end screen retries up to 8 s until FoundMatch) |
| EventGetActiveMatches | JSON | `{MatchesV3:[]}` (no reconnect into a running match) |
| Rank*, Renewal*, Quest*, PeriodicRewards*, Carousel*, PrizeWalls, NetDeckFolders, PreferredPrintings, StaticContent, StoreGetEntitlementsV2, MercGetStoreStatusV2 | JSON | neutral/empty objects (see FrontDoor.java) |
| GetVoucherDefinitions, GetPlayerInbox, GetWpnNotificationPreferences, ChallengeReconnectAll, Cosmetics* | proto | default instances |
| MercGetSkusAndListings, RankEvaluatePayoutsV2 | - | ServerError on purpose: keeps the Store tab locked (an empty catalogue opens a Store page that hangs) / no season payout popup |
| LogBusinessEvents(V2) | JSON | "" |
| anything else | - | ServerError + `FD UNHANDLED <cmd>` in the capture and server log |

### Play (bot match)
Play blade "Find Match" -> "Bot Match" queue (GetPlayBladeQueueConfig + the AIBotMatch event) ->
`HomePageContentController.JoinMatchMaking("AIBotMatch", deckId)` -> deck saved (last played) -> `EventAiBotMatch` ->
`Matchmaking.SetupBotMatch` loads the duel scene -> MatchCreated push -> Match Door connect with that matchId. The Front
Door publishes `forge.mtga.MatchSetup` (human deck GrpIds -> Forge names + the chosen printings, AI precon) which the next
Match Door connection plays. After the game: IntermissionReq/MatchCompleted -> end screen -> `EventGetMatchResultReport`
-> exit -> wrapper reload (StartHook again) -> home page.
GRE detail found on the way: the client replaces its GameInfo with any GameInfo a diff carries, so a diff with only
`stage=Play` reset MatchState to None and hid Concede for the whole game; StateSync now always sends a complete GameInfo.

## Match Door (GRE)
- Same framing. Body = `ClientToMatchServiceMessage` / `MatchServiceToClientMessage` (MatchServiceMessages.proto).
- Handshake (SharedClientCore/GreClient.Network/GREConnection.cs):
  1. TCP+TLS open -> client sends `AuthenticateRequest{ClientId, ClientInfo, PlayFabSessionTicket, InactivityTimeoutMs}`
  2. server -> `AuthenticateResponse{ClientId, InactivityTimeoutMs}` => state ConnectedToMatchDoor
  3. client -> `ClientToMatchDoorConnectRequest{ClientToGreMessageBytes(ConnectReq), MatchId, McFabricUri}`
  4. server -> `GreToClientEvent{GreToClientMessages:[ConnectResp, ...]}`; room state via `MatchGameRoomStateChangedEvent` (Playing / MatchCompleted, FinalMatchResult)
  - Debug path: client -> `CreateMatchGameRoomRequest`, server -> `CreateMatchGameRoomResponseV2{McFabricUri}`; matchId = last path segment of McFabricUri (regex `/([0-9a-z-]+)$`).
  - In-game: client wraps `ClientToGREMessage` as `ClientToMatchServiceMessage{type=ClientToGremessage|ClientToGreuimessage, Payload}`.
- GRE -> client types: GameStateMessage(1), ActionsAvailableReq(2), ChooseStartingPlayerReq(6), ConnectResp(7), GroupReq(11), MulliganReq(15), SelectNreq(22), DeclareAttackersReq(26), DeclareBlockersReq(28), AssignDamageReq(30), SelectTargetsReq(34), PayCostsReq(36), IntermissionReq(37), SelectReplacementReq(39), NumericInputReq(43), CastingTimeOptionsReq(46), SelectFromGroupsReq(48), SubmitDeckReq(53), TimerStateMessage(56) ... (full list: GREMessageType enum)
- client -> GRE: ConnectReq(1), PerformActionResp(15), MulliganResp(13), DeclareAttackersResp(30), SubmitAttackersReq(31), DeclareBlockersResp(32), SubmitBlockersReq(33), SelectTargetsResp(36), SubmitTargetsReq(37), SelectNresp(18), GroupResp(12), ConcedeReq(7), ChooseStartingPlayerResp(24), SubmitDeckResp(54) ...

## Debug launcher (NOT usable as-is)
`View_Login` / `DebugMatchCreation` / `DuelSceneDebugLauncher` implement a direct "connect to Match Door host:port, create match from MatchConfig (decks, players, bots)" flow, bypassing Front Door.
BUT the shipped build contains only 14 scenes (Bootstrap, AssetPrep, EmptyScene, NavBar, HomePage, ... no DuelSceneDebugLauncher). The C# classes (`DebugMatchCreation`, `MatchManager.CreateMatch`, `GREConnection.CreateMatch`) are still present and reusable by a patch that calls them without the scene.
Also: debug path server errors with `NotAuthorized` unless account has role `MTGA_DEBUG` (`AccountInformation.HasRole_Debugging`).

## Client patches (bridge/Patcher, Core.dll copy)
1. `Bootstrap.Awake`: `Coroutine_GameStartup()` -> `ForgeBridge.LocalStartup.Run`.
2. `WrapperDeckUtilities.TryImportDeck`: import text first goes through `ForgeBridge.DeckImport.Rewrite` (loose name
   matching, cards the client lacks removed and listed in a popup). Without it, import is an exact English-title match and
   one unknown card aborts the import.
3. `DeckFormatUtil.GetBestFormat`: result passed through `ForgeBridge.DeckFormats.Best` (narrowest Forge format whose card
   rules the imported deck meets; the client's own list only knows Arena formats).

## Card data
`Downloads/Raw/Raw_CardDatabase_*.mtga` is SQLite: Cards(GrpId PK, TitleId, Types, Subtypes, Colors, AbilityIds, Power, Toughness, ...), Abilities, Localizations_*.
GrpId is what GRE messages use for all card identity; names via Localizations_enUS(TitleId).

## Options to get a local game (decided: C = local Front Door + EventAiBotMatch; B stays available as FORGEBRIDGE_MODE=duel)
A. Stub Front Door server (TCP/TLS JSON, subset of CmdTypes) + local doorbell HTTP + patched IAccountClient. Full client UX incl. deck builder works. Larger server surface.
B. Patch Core.dll with a "local mode" entry that skips login/doorbell and calls the DebugMatchCreation-equivalent directly against the local Match Door. Smallest server surface; no menus.
C. Hybrid: A for minimum startup commands + `EventAiBotMatch`, later `Challenge*` for P2P.
