# Player profile: what the client treats as per-player data

Findings from `docs\decompiled` (read only) and the user's own install (`Downloads\ALT`, `Downloads\Raw`). Nothing of Wizards' game is
copied into the repo or the package: the catalogue is read from the player's install at run time (section 2).

## 1. Inventory of profile data

"Who needs it": **self** = the player's own client, **opp** = the opponent's client (rendered from `MatchCreated`), **srv** = only the server.
Encoding: **JSON** = `Cmd.JsonPayload`/`Response.JsonPayload` (Newtonsoft), **proto** = `Any` payload (schemas in `docs\proto\fd`).

| Data item | Command / message | Enc. | Shape (notes) | Who | Client code that reads / writes it |
|---|---|---|---|---|---|
| PersonaID (account id) | `IAccountClient.AccountInformation.PersonaID` (our `LocalAccountClient`) | C# | string. Sent in FD `Authenticate` as `PlayerId`; the client keys its `MDNPlayerPrefs` (PlayerPrefs) by it | self, srv | `FrontDoorConnectionManager`, `MDNPlayerPrefs`, `PAPA`, `ConnectionManager` |
| DisplayName | `AccountInformation.DisplayName` | C# | `Name#12345` (the `#` suffix is drawn grey by `ProfileUsernameDisplay`) | self | `ProfileUsernameDisplay`, `TitleSelectPanel`, `NavBarController`, `Matchmaking` (local `PlayerInfo`) |
| Session / auth | FD `Authenticate`, `Attach` | JSON | answer `FrontDoorSessionResp{SessionId, Attached, PlayerConditions}` | self | `FrontDoorConnectionAWS` |
| Catalogue of every cosmetic ("available") | FD `StaticContent` (req `StaticContentReq.AvailableCosmeticsHash`) | JSON | `{AvailableCosmetics:{Hash:int, Object:{ArtStyles,Avatars,Pets,Sleeves,Emotes,Titles}}}`; entries below; client skips re-sending when the hash is unchanged | self | `StaticContentController.InitProviders` -> `CosmeticsProvider.SetAvailableCosmetics` -> `Mercantile.Convert*` (avatar/pet/sleeve/emote catalogues) |
| Owned cosmetics | StartHook `InventoryInfo.Cosmetics` (+ `InventoryUpdate.Cosmetics` pushes) and `CosmeticsGetPlayerOwnedCosmetics` (`GetPlayerOwnedCosmeticsReq` -> `GetPlayerOwnedCosmeticsResp{OwnedCosmetics}`, only used when rejoining a running match) | proto | `CosmeticsClient{ArtStyles,Avatars,Pets,Sleeves,Emotes,Titles}` | self | `AwsInventoryServiceWrapper` -> `CosmeticsProvider.SetPlayerOwnedCosmetics`; selectors show only owned items; art styles drive the card-style picker |
| Equipped cosmetics ("preferred") | StartHook `PreferredCosmetics` (+ `PreferredCosmeticsCacheVersion`), `CosmeticsGetPreferredCosmetics` (`GetPreferredCosmeticsRequest`) | proto | `PreferredCosmetics{Avatar,Sleeve,Pet,Title,Emotes[]}`, each an entry message with only `Id` (pet also `Name`, `Variant`) | self | `AwsStartHookServiceWrapper` -> `CosmeticsProvider.SetVanitySelections` (`ClientVanitySelectionsV3`); no emotes sent = the 5 default phrases |
| Set avatar / sleeve / pet / title / emotes | `CosmeticsSetPreferredAvatar/Sleeve/Pet/Title/Emotes` (`SetPreferred*CosmeticRequest{Id}`, emotes `{Ids[]}`) | proto | answer: the new `PreferredCosmetics` (the client copies it into its selections) | self -> srv | `CosmeticsProvider.Set*Selection`, `CosmeticSelectorController`, `AvatarSelectorOpener`, reward popups (`AvatarReward`...), `SceneLoader` (default avatar `Avatar_Basic_AjaniGoldmane` when none) |
| Purchase a cosmetic | `CosmeticsPurchaseCosmetic` | proto | not offered offline (store off) | - | `Mercantile` / store |
| Deck-level cosmetics | `DeckSummaryV3.PreferredCosmetics` (`PreferredCosmeticsOnDeck{Avatar,Sleeve,Pet,Title: id string, Emotes[]}`), carried by StartHook `DeckSummaries`, `DeckGetDeckSummariesV3`, `DeckUpsertDeckV3` | JSON (upsert/summaries), proto (StartHook) | stored verbatim in `data\decks\<id>.json` already; empty string = "use the profile's" | self, srv | `DeckDetailsPopup`, `CosmeticsPerDeckView`, `DeckManagerController`. The real server resolves deck override > profile when it builds `MatchCreated` |
| Player preferences | `GetPlayerPreferences` / `SetPlayerPreferences` | JSON | `{Preferences:{key:string}}` (play blade selection, tutorial flags...) | self | `PlayerPrefsDataProvider` |
| Rank (own) | `RankGetCombinedRankInfo`, `RankGetSeasonAndRankDetails` | JSON | constructed/limited class+level+step+W/L/D, percentile, leaderboard | self | `ProfileContentController`, `CombinedRank` |
| Achievements / home page | StartHook `HomePageAchievements`; static content `AchievementsMetadata` | proto / JSON | empty; kill switch `AchievementSceneStatus` | self | achievements scene |
| Inventory | StartHook `InventoryInfo` (gold, gems, wildcards, `Cosmetics`...) | proto | zeros | self | `InventoryManager` |
| Decks | StartHook `DeckSummaries`+`DecksInternal`, `DeckGetDeck*`, `DeckUpsertDeckV3`, `DeckDeleteDeck` | proto / JSON | see protocol.md | self, srv (match) | `DecksManager` |
| Play blade queues | `GetPlayBladeQueueConfig` | JSON | one bot queue | self | `HomePageContentController` |
| What the opponent's client renders of the other player | FD push `MatchCreated.MatchInfoV4.PlayerInfos[]` (`ClientMatchPlayerInfoV2`) | JSON push | per seat: `ScreenName, RankingClass (Spark..Mythic), RankingTier, MythicPercentile, MythicLeaderboardPlace, IsWotc, SeatId, TeamId, Commanders[], CosmeticsSelection:PreferredCosmetics` | self + opp | `MatchPlayerConfig.FromClientMatchPlayerInfo` -> `Matchmaking.ConvertFromConfig` -> `MatchManager.PlayerInfo{ScreenName, Avatar/Sleeve/Title/PetSelection, EmoteSelection, ranks}` for both seats. An avatar id with no bust image is replaced by a random default avatar |
| Same, challenge flow | `Challenge*` (`ChallengePlayer`, `ChallengeCommunicationWrapper`, `PVPChallengeController`) | JSON | `PlayerId`, `DisplayName`, `ClientVanitySelectionsV3` (title as loc key) | self + opp | friend challenges; not used by our P2P (bot-queue hijack) |
| In-game rendering of cosmetics | not on the GRE wire: `ConnectReq`/`ConnectResp` carry no cosmetics (`ProtoVersion_Cosmetics` is only a version). Everything comes from `MatchManager.LocalPlayerInfo/OpponentInfo` (filled from `MatchCreated`) | - | avatar -> `ALT_Avatar.*Payload` (Cosmetic_AvatarId), sleeve -> `ALT_Card.Sleeve` (`Cosmetic_SleeveId`), pet -> `ALT_Cosmetic.PetPayload` (`PetId`+`PetVariantId`), title -> VS screen (`CosmeticsUtils.TitleLocKey` via the LOCAL catalogue), emotes: own equipped list from `LocalPlayerInfo.EmoteSelection` | self + opp | `MatchSceneManager` (blackboard `PlayerInfoMatch`), `VSScreen`, `AccessoryController*`, `EmoteManager` |
| Emote use in game | GRE `ClientToGREUIMessage` (`ClientMessageType_UIMessage = 53`, `uiMessage{onChat{text = emoteId}}`) client -> server; server -> **the other seat** as `GREToClientMessage{type UIMessage (52), uiMessage{onChat}}`. The sender shows its own emote locally; the opponent's `OpponentDialogController` shows the received one (unknown ids and muted emotes are dropped) | proto (GRE) | pet taps use `onGenericEvent{category "AccessoryInteraction", payload}` the same way; card hover = `onHover{objectId}` | opp | `UIMessageHandler`, `UiMessageTranslator` |
| Friends / chat / reporting | `Social*`, `ReportPlayer`, Hasbro Go | - | off by kill switches | - | |

### Cosmetic entry shapes (static content JSON; owned/preferred are the same fields as protobuf)

| Kind | Fields | Id convention |
|---|---|---|
| Avatar `CosmeticsAvatarEntry` | `Id, AcquisitionFlags` | `Avatar_Basic_<Name>` (329 in this build); loc `MainNav/Profile/Avatars/<id>_Name`; bust/thumb/full-body images via `ALT_Avatar` |
| Sleeve `CosmeticsSleeveEntry` | `Id, AcquisitionFlags` | `CardBack_<set>_<n>` / `CardBack_Basic_*` (1,254) + the built-in `CardBack_Default`; loc `MainNav/DeckBuilder/Sleeves/<id>` |
| Pet `CosmeticPetEntry` | `Id, Name, Variant, AcquisitionFlags` | `Id = Name + "." + Variant` (`AFR_Beholder.Level1`); the client builds that id itself in `SetPetSelection(name, variant)` |
| Emote `CosmeticEmoteEntry` | `Id, Page (Phrase/Sticker), FlipType (None/Reply/Priority), Category, Treatment, AcquisitionFlags` | `Phrase_<set>_<Name>` (257), `Sticker_<set>_<Name>` (146); default order `Phrase_Basic_Hello, _Nice_Thanks, _Thinking_YourGo, _Oops_Sorry, _GoodGame`; caps 15 phrases / 10 stickers |
| Title `CosmeticTitleEntry` | `Id, InternalName, LocKey, AcquisitionFlags` | loc keys `Titles/<Group>/<Name>` in `Raw_ClientLocalization` (`_RewardKey` variants are not titles); `NoTitle` = none |
| Art style `CosmeticArtStyleEntry` | `Id, ArtId, Variant, ExplicitGrpIds[], AcquisitionFlags` | `Id = <artId>.<variant>`; client card DB `AltPrintings.SkinCode` / `AltToBasePrintings` list the styles (phase 2) |

`AcquisitionFlags` in JSON is the enum NAME (`None`, `DefaultLoginGrant`, `Event`...), parsed case-insensitively. `DefaultLoginGrant` marks
the entries every account starts with (the random-avatar fallback and the default emotes use it).

## 2. Where the cosmetic catalogue lives (all in the player's install)

The card DB has no cosmetic tables. The ids are the keys of the asset lookup trees in `MTGA_Data\Downloads\ALT\ALT_*.mtga` (plain JSON:
`{tree: {Nodes[], Connections[]}}`; an extractor node's `Connections` entry maps each possible value (= the cosmetic id) to a node):

| Cosmetic | File :: tree :: extractor node | Count (build 2026.64) |
|---|---|---|
| Avatars | `ALT_Avatar :: ALT_Avatar.ThumbnailPayload :: Cosmetic_AvatarId` | 329 (324 `Avatar_Basic_*`: the 5 `NPE_*` tutorial opponents are left out) |
| Sleeves | `ALT_Card :: ALT_Card.Sleeve :: Cosmetic_SleeveId` (about 60 extractor nodes: the tree is chunked, take the union) | 1,254 (+ the built-in `CardBack_Default`) |
| Pets | `ALT_Cosmetic :: ALT_Cosmetic.PetPayload :: PetId` (child: one `PetVariantId` node per pet, keys `Level1..5`, `Skin1..5`, `Uncommon`) | 99 pets, about 190 pet+variant ids |
| Phrases | `ALT_Emotes :: ALT_Emotes.EmoteLocKey :: Emote_IdActual` | 257 |
| Stickers | `ALT_Prefab :: ALT_Prefab.EmoteViewPrefab :: Emote_Id` (keys starting `Sticker_`) | 146 |
| Titles | `Raw_ClientLocalization` (SQLite, table `Loc`): keys `Titles/<Group>/<Name>` without `_RewardKey` | 40 |
| Art styles | card DB `AltPrintings` (`AltGrpId, IsAltArt, SkinCode`) + `AltToBasePrintings` | 4,026 (phase 2) |

Read at run time by `forge.mtga.profile.CosmeticCatalog` from `<client>\MTGA_Data\Downloads` (the same install the card DB comes from); the
package contains only the reader. Loc/name text and images come from the client itself once an id is selected.


## 3. Profile model (`forge.mtga.profile`)

One versioned JSON document per player, `data\profile.json` (written atomically; an unreadable file is kept as `profile.json.bad-<time>` and replaced):

```json
{
  "version": 1,
  "playerId": "791602cb-6609-4222-9040-45416765eb79",
  "displayName": "Alice",
  "tag": "85191",
  "equipped": { "avatar": "Avatar_Basic_JaceBeleren", "sleeve": "CardBack_Default", "pet": "AFR_Dragon.Skin1",
                "title": "Title_Core_AchievementHunter", "emotes": ["Phrase_Basic_Hello", "..."] },
  "owned": { "all": true, "avatars": [], "sleeves": [], "pets": [], "emotes": [], "titles": [] },
  "rank": { "rankClass": "Bronze", "tier": 4 },
  "preferences": { "<client key>": "<value>" },
  "created": "...", "updated": "..."
}
```

- `playerId`: a GUID made on the install's first start (replaces the fixed `LOCALPLAYER...` id); stable across restarts.
- `displayName`: no `#`, 24 characters at most (`Profile.cleanName`); `tag`: five digits, stored once; the client shows `Alice#85191`.
- `preferences`: the client's `GetPlayerPreferences` map (it used to be `data\preferences.json`, imported once when the profile is first created).

| Field | Visibility | Why |
|---|---|---|
| `playerId`, `displayName` (+ `tag`), `equipped.*`, `rank` | **PUBLIC** = `PublicProfile`, what an opponent gets (peer HELLO / HELLO_OK; later a web service's player card) | what the other client draws: name, avatar, sleeve, pet, title, emotes, rank badge |
| `owned.*` | private (server side; the client only learns its own list) | the opponent never needs it; a web service keeps the ledger |
| `preferences`, `created`, `updated`, `version` | private | |
| decks | not in the profile: `data\decks\<id>.json`. Deck-level cosmetics in `Summary.PreferredCosmetics` override the profile's for a match, only where owned | |

`owned.all = true` (default) means "everything the client has", like the collection owns every card x4. Setting it to `false` and listing ids gives a real
inventory (the defaults stay owned); the Set* commands refuse ids that are not owned, which is how a web service will enforce ownership.

Code map:
- `Profile`, `PublicProfile`, `ProfileStore` (interface: `load()` returns a copy, `update(Consumer<Profile>)` is atomic), `FileProfileStore` (the offline implementation; the Cloudflare one only has to implement the interface), `CosmeticCatalog` (section 2).
- `forge.mtga.fd.ProfileApi`: the wire layer. Builds `StaticContent` (catalogue), `InventoryInfo.Cosmetics` / `GetPlayerOwnedCosmeticsResp` (owned), `PreferredCosmetics` (equipped), the `CosmeticsSelection` of a `MatchCreated` player; applies the `Cosmetics*` commands after checking catalogue and ownership; resolves deck overrides; sanitizes an opponent's public profile against this client's catalogue.
- `forge.mtga.fd.FrontDoor`: the handlers (below). `ForgeMtgaMain` creates the store, applies `-Dmtga.name` (the launcher's name box) and publishes the name as `mtga.name` for the engine seats.
- Client bridge: `LocalProfile` (reads `playerId`, `displayName`, `tag` from `profile.json`: `FORGEBRIDGE_DATA` or `<client>\..\data`), `LocalAccountClient` (PersonaID / DisplayName from it), `CosmeticCatalogs` (below).
- Launcher window (`MTGA-Forge.exe`): the name box is prefilled from the profile, shows "Player ID xxxxxxxx - shown as Name#12345", and its value goes to the server as `-Name` -> `-Dmtga.name` -> `profile.displayName`.

## 4. Behaviour

Front Door commands answered from the profile (everything else stays "FD UNHANDLED"):

| Command | Answer |
|---|---|
| `Authenticate` | session as before; a `PlayerId` different from the profile's is logged |
| `StaticContent` | `AvailableCosmetics{Hash, Object{Avatars, Sleeves, Pets, Emotes (Page Phrase/Sticker), Titles, ArtStyles: []}}` from the catalogue; `Hash` = catalogue fingerprint |
| `StartHook` | `PreferredCosmetics` = equipped, `InventoryInfo.Cosmetics` = owned; `InventoryInfo.SeqId` grows on every call (the client ignores an owned list whose seq did not grow) |
| `CosmeticsGetPlayerOwnedCosmetics`, `CosmeticsGetPreferredCosmetics` | owned / equipped |
| `CosmeticsSetPreferredAvatar/Sleeve/Pet/Title/Emotes` | validate (in the catalogue, owned, emote caps 15 phrases + 10 stickers, no duplicates), persist, answer the new `PreferredCosmetics`; a refused change answers the unchanged selection |
| `GetPlayerPreferences` / `SetPlayerPreferences` | `profile.preferences` |
| `MercGetSkusAndListings` | success with every listing type EMPTY (it was an error): the client builds its cosmetic catalogues (profile pickers, deck cosmetics, emote data) from this call |
| kill switches | new `StoreStatus` (store off; the store used to be locked only because the call above failed). Unchanged: Social*, WPN store, player reporting, Hasbro add/remove friends, Achievements, Tournament, Booster. Nothing had to be switched back on: every cosmetic feature works with the existing switches |
| `MatchCreated` push | each `PlayerInfos[]` entry carries `RankingClass/Tier` and `CosmeticsSelection` (own = profile + deck override; AI = random default avatar and the default sleeve; P2P opponent = the peer's public profile, sanitized) |

Client side: the cosmetic pickers read `WrapperController.Store.{Avatar,Pet,CardBack,Emote}Catalog`, which the client only fills when the store is enabled. `ForgeBridge.CosmeticCatalogs`
(a coroutine started by `LocalStartup`, no Cecil patch) calls the client's own `GetMercantileCollections()` and stores the result in the (disabled) store manager's `MercantileCollections`; it repeats for a reloaded wrapper.

Verified in the client (bot match): own avatar and title on the player panel, sleeve on the library, the 3D pet beside the avatar, the equipped emotes in the emote wheel (click your avatar).
Profile > Customization: Avatars, Companions (pets), Card Sleeves, Emotes and Titles list the whole catalogue; choosing an avatar persisted `profile.json` (server log `profile: avatar -> ...`).

P2P (`forge.mtga.peer`, protocol version 2): the guest's HELLO carries `profile` (its public profile with the deck's cosmetics laid over it), the host's HELLO_OK carries the host's; each server sanitizes the other's
against its own catalogue (unknown ids are dropped) and puts it in its client's `MatchCreated`, so both clients draw the opponent's avatar, sleeve, pet, title and name.
Emotes: the client sends `ClientToGREMessage{UIMessage, uiMessage{onChat{text = emoteId}}}`; `ForgeMatch.relayUi` hands `OnChat` (emotes) and `OnGenericEvent` (pet gestures) to the OTHER human seat as a
`GREToClientMessage{UIMessage}` (no echo: the sender draws its own emote locally); `OnHover/OnSelect` name the sender's instance ids and are dropped. The AI never emotes.

Tests (headless, no client): `forge.mtga.fd.ProfileCheck` (store round trip, stable id, legacy preferences import, unreadable file, names, catalogue vs static content, Set* validation and ownership, emote caps,
deck override, sanitizing, the public view has no private fields); `SelfPlay -Dselfplay.peer=true` (HELLO/HELLO_OK carry the public profiles unchanged, emotes relayed to the other seat only, hover not relayed) and
`-Dselfplay.pvp=true` (same, in process). `tools\verify.ps1` runs them as `sp-profile`, `sp-peer`, `sp-pvp`.

Known gaps: card styles (`ArtStyles`) are not offered, the card-style picker stays empty (phase 2, from `AltPrintings`); the AI does not emote; the title tab of Customization is labelled "Companions" by the
client's own loc table (not ours); no reconnect; titles are offered as owned although they are meant to be earned (no achievements offline); the nav bar shows a small orange marker on the (still locked) Store tab
once the catalogues are filled.

## 5. What the web service must store and serve (next phase)

Principle: the service implements `ProfileStore` remotely and nothing above it changes; the local file stays as the offline cache. The client bridge keeps reading only `playerId` and the name
(or gets them from the login response); everything else already travels over the Front Door protocol the local server speaks.

**Accounts** (Cloudflare Worker + D1 / Durable Objects): `accountId` (= `playerId`, the GUID), an auth credential (passkey / OAuth, not an MTGA login), `displayName` + `tag` (unique as a pair; the service
allocates the tag), `created`, status (banned / muted), optional contact. One anonymous device account per install at first, upgradable later.

**Profile document**: exactly `Profile` above, split by writer: `equipped` and `displayName` written by the player (validated against `owned` and the catalogue), `owned` written only by the service
(grants: defaults, achievements, events, store later), `rank` only by the service, `preferences` by the player (size-limited). Versioned (`version`, `updated`, optimistic concurrency).
The catalogue is not stored per player: it is the set of ids a client build has, which differs between MTGA builds, so the service stores ids and validates leniently (an id the viewer's client cannot
draw is dropped by the viewer, as `ProfileApi.sanitize` does now).

**Public view** `GET /players/{playerId}/public` -> `PublicProfile` (playerId, name, avatar, sleeve, pet, title, emotes, rankClass, rankTier; nothing private), served to the opponent's client or server
at match creation. For P2P the host should not trust the guest's HELLO: the guest sends a short-lived token (service signature over the public profile + `playerId` + expiry) instead of plain JSON and the
host verifies it with the service's public key (offline: no signature, accepted as today).

**Rating / rank per format**: ratings keyed by `(playerId, ratingPool)`; pools = the constructed formats the client lists (Standard, Pioneer, Modern, Legacy, Vintage, Pauper, Historic, Timeless, Alchemy,
Commander casual...) x best-of-1 / best-of-3, plus limited (per set event); Glicko-2 or Elo with `rating, deviation, matches, wins, losses, draws, lastPlayed`; season id; derived `rankClass/tier/step`
(Spark..Mythic) and mythic percentile / leaderboard place for `RankGetCombinedRankInfo`, `RankGetSeasonAndRankDetails` and `MatchCreated.PlayerInfos`.

**Matchmaking and results**: one queue per pool (`EventEnterPairing` / the play-blade entry), pairing by rating window and wait time; the answer is the same `MatchCreated` data plus the host's peer address
(or a relay); a match record `(matchId, pool, players, deck hashes, host, startedAt)`; the result is reported by BOTH clients (the host engine's result and the guest's own `IntermissionReq`), accepted when they
agree and flagged otherwise; the service then updates ratings and answers the `rankUpdates` the end screen reads (`EventGetMatchResultReport`).

**Other state worth keeping server side**: deck list sync (optional; the local `Summary`/`Deck` JSON as is, deck-level cosmetics included), friends and presence later (the Social kill switches stay on until then),
a cosmetics grant ledger (who got what, from which source = `AcquisitionFlags`), moderation (reports, name filtering), rate limits per account/IP, export/delete of the profile document.

**Migration path**: `RemoteProfileStore` = HTTP client with local write-behind to `profile.json` (offline play keeps working; conflicts resolved by `updated`); the launcher's name box calls the service's rename
endpoint instead of `-Dmtga.name`; `LocalProfile.cs` keeps reading the id and name from the file the launcher/server writes.
