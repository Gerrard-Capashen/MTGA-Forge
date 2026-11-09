# Development notes

The things that are hard to learn from the code alone: how the client consumes game state, how the card database is extended, what breaks when MTGA updates, and how to test. Read [architecture.md](architecture.md) first for how the pieces fit.

## What exists

| Area | Where | State |
|---|---|---|
| Patching a copy of the install | `tools/forge-patch.ps1`, `bridge/Patcher` | Ordered, idempotent steps recorded in `<target>\forge-patch\state.json`; the install is only read |
| Client-side code | `bridge/ForgeBridge` | Local login and Front Door, social features, UI tweaks (formats, decks, queues, mastery, tooltips) |
| Lobby (Front Door) | `forge.mtga.fd` | Decks, deck import, Bot Play, Casual and Ranked queues, quick and premier drafts, events, challenges, profile and cosmetics |
| Games (Match Door) | `forge.mtga.net`, `forge.mtga.forge` | Forge game state as GRE messages, client prompts as Forge decisions, best of three with sideboarding |
| Card database | `forge.mtga.CardDbBuilder` | Adds Forge cards the client lacks; art for them from Scryfall |
| Online play | `forge.mtga.peer`, `coordinator/` | Pairing, relay, draft tables, results, rank, mastery, quests, friends |
| Formats | queues and deck formats | Standard, Pioneer, Modern, Legacy, Vintage, Pauper, Premodern, Brawl, Momir Basic, the Archenemy event |

## How the client consumes game state

The client is thin: it renders `GameStateMessage`s and answers prompts. The server does all the rules. What it needs from us:

- **Diffs, not snapshots.** A diff is merged into the last full state. The client derives events by itself only for zone membership, power/toughness, damage, attack and block state, summoning sickness, grpId and visibility. Everything else animates only through annotations:
  - turn change: `NewTurnStarted` (affector = active seat); phase ladder: `PhaseOrStepModified`
  - tap/untap: `TappedUntappedPermanent`; life: `ModifiedLife` (the delta; for combat damage the affector must equal the `DamageDealt` source, or the client zeroes the damage)
  - counters: `CounterAdded`/`CounterRemoved` plus a persistent `Counter`
- **Zone moves.** Every zone change gets a new instance id. `ObjectIdChanged(orig, new)` must be followed immediately by the `ZoneTransfer` of the new id, with the transfer reason as its category.
- **Abilities.** Rules text, activation buttons and the loyalty UI come only from `GameObjectInfo.uniqueAbilities`. `AbilityMap` maps a Forge `SpellAbility` to the client's ability id.
- **Playable highlight.** A Cast or Activate action with a mana cost counts as affordable only if it carries `autoTapSolution.autoTapActions`.
- **Prompts.** `OptionalActionMessage` gives "Take action"/"Decline". Prompt text comes from the Prompts table of the card database (`CardDb.promptId` looks up the exact text). Modal choices are `SelectNReq` over ability ids, scry is `GroupReq`, ordering is `OrderReq`, X is `NumericInputReq`.
- **Target arrows.** While targets are chosen the client draws the arrow from `SelectTargetsReq.sourceId` (the spell on the stack). Once the spell or ability is on the stack, the arrow comes only from a persistent `TargetSpec` annotation: affector = the stack object, affected = its targets, details `index` (1-based) and `abilityGrpId` (the client's parser throws without them). `StateSync.addTargetSpecs` sends one per targeting part. The arc is a Dreamteck `SplineRenderer` ribbon turned towards `Camera.main` (under URP `Camera.current` is null). Any other camera tagged `MainCamera`, such as AssetPrep's when a duel is reached without the home page, makes it edge-on, so only the 3D arrowhead shows. `ForgeBridge.DuelCamera` unloads AssetPrep when a duel scene loads. The debug click `arrows` (FORGEBRIDGE_CLICKS) dumps cameras, scenes, splines and renderers.
- **Mulligan.** Forge's tuck is deferred: after keeping on N mulligans, the client gets the "Return N cards" `GroupReq`.
- **Dungeons.** The client tracks a player's dungeon only through a persistent `DungeonStatus` annotation (affector = seat, details `CurrentDungeon` grpId, `CurrentDungeonZCID` instance, `CurrentRoom` room ability id). Choosing a dungeon is a `SelectNReq` of dungeon card grpIds (`DungeonSelectWorkflow`). Choosing a room is a `SelectNReq` of room ability ids. For the three AFR dungeons it opens the map picker (`DungeonRoomSelectWorkflow`, from a hardcoded id list in `SelectNRequest`), which throws without `DungeonStatus`. Undercity is our generated row, so its rooms open the generic ability browser. As an experiment, its row carries the AFR dungeon frame with only its 9 room abilities.

Every Forge decision that should be the player's goes through the `MtgaController` overrides in `ForgeBackend`. A decision left to Forge's default falls back to the AI, which shows up as "the game chose for me". Check the decompiled client (`tools/decompile.ps1`) for what it expects before guessing.

## The card database

`CardDbBuilder` copies the client's own card database (snapshot in `<target>\forge-patch\carddb\pristine`) and adds every Forge card the client lacks that is legal in the configured formats:

- New rows clone a template card. Ids start at 9,000,000 (GrpId, ArtId, ability ids) and localization ids at 900,000,000, so they never clash with the client's.
- Oracle text becomes one ability per line. A line equal to an existing client ability reuses it (icons, server mapping); otherwise a new ability row is made.
- **Gotcha:** id-list columns (`ModalChildIds`, `HiddenByAbilityIds`, ...) must be NULL, never an empty string. The client parses every comma-separated part, and the deck builder crashes otherwise.
- The result is installed as a new `Raw_CardDatabase_<md5>.mtga` with a new main manifest listing it. The server reads the same file, so collections, formats, deck import and games pick the cards up.
- Art for generated cards (ArtId 9,000,000+) is served by the local art server from Scryfall. Split cards and Rooms get one art id per half.
- The database file name is the **card DB fingerprint** used for online pairing: two players only meet with the same MTGA build and the same builder.

## When MTGA updates

The patcher's hooks name methods in `Core.dll`. After an MTGA update:

1. `forge-patch.ps1 -Check` notices the new build, and the launcher re-patches.
2. `Patcher check-bridge` verifies that every client member ForgeBridge references still exists. A missing one stops patching with a clear error and nothing is started.
3. Typical breakages and fixes, from past updates:
   - a client class lost a constructor or method we call: replace the dependency with our own subclass (for example `OpenPromise<T>` instead of `SimplePromise<T>`)
   - promoted-set art was renamed: the patcher finds boot logo and loading textures by name pattern
   - new cosmetics appeared: the mastery track inserts them (it stays append-only for what players already earned)
4. Run `tools\gen-protos.ps1` against the new client and rebuild: new enum values appear, and placeholder names sometimes get real names.
5. A new set brings new booster collations. `CollationIds` is kept by hand from the client's `CollationMapping` enum (`tools\decompile.ps1 -Assemblies SharedClientCore -Force`). Without the set's entries the event page shows no pack art.

## Merging upstream Forge

`forge/` tracks Card-Forge/forge as the `upstream` remote; merge `upstream/master` into `mtga` with a merge commit (the clone is shallow, so commit counts against upstream are meaningless; use `git merge-tree --write-tree mtga upstream/master` for a dry run).

- Our engine changes are a few hooks (`ComputerUtil`, `GameRules`, `CopyPermanentEffect`, `MakeCardEffect`, `Card`, `Cost`). They usually merge cleanly; the real risk is API drift in what `forge-gui-mtga` uses, which only the compile shows (for example `getUserAgent()` moving from `BuildInfo` to `IGuiBase`).
- `MtgaController` extends `PlayerControllerAi`, so upstream AI changes reach the human seat: check new `PlayerController` hooks against what it overrides.
- A new set in Forge's editions (with a booster template, in Standard) becomes `QuickDraft_<SET>` and the premier set on its own once the client has its cards. Add its name and blurb to `SetBlurbs.cs`.
- In a dev tree `forge-patch.ps1` rebuilds the card DB only when `CardDbBuilder.java` changes, not when Forge's card data does: after a merge run it with `-BuildServer -Only bridge,patch,carddb,manifest`. The new DB changes the card DB fingerprint used for online pairing.

## Testing

- **Headless self-play** (`forge.mtga.SelfPlay`): a fake client answers every request at random and checks each diff the way the real client merges it (diff chaining, zone and id consistency, annotations present, request ids valid). It runs through `tools\verify.ps1 -SkipClient`, with recipes for every queue and format.
- **Rigs** ([rigs.md](rigs.md)): deterministic test matches with fixed hands and boards, for reproducing a card interaction in the real client (`tools\play.ps1 -Rig <name>`).
- **Real client:** `tools\play.ps1` for one player against the AI. For online play, two instances on one PC:
  ```powershell
  tools\launcher.ps1 -Folder D:\MTGA-P1 -Instance 1 -Name Alice -FriendCode test
  tools\launcher.ps1 -Folder D:\MTGA-P2 -Instance 2 -Name Bob -FriendCode test
  ```
  Each folder is a full patched copy (about 14 GB). `-Instance N` shifts the ports by 100·N.
- **Coordinator:** `npm run dev`, then `npm test` (smoke, accounts, friends, challenges, results, rank, mastery, quests, tables, names). Test accounts are named `zztest...`; `npm run db:clean` removes them.
- **Logs:** the server log is in `<folder>\logs`, the client's in `%USERPROFILE%\AppData\LocalLow\Forge Bridge Project\<product>\Player.log`. Dev runs can capture every Front Door and GRE message to `tools\capture\` (`play.ps1`).

## Known gaps

- Some Forge decisions still fall back to the AI where the client has no matching prompt. Report the card; the fix is usually a new `MtgaController` override.
- Animations for a few rare state changes (some emblems, tokens moving to exile) can be missing even though the game state is right. Self-play reports them as sync findings.
- Display-only data from an online opponent is self-reported: their rank badge, cosmetics, and the names in a premier draft pod ([architecture.md](architecture.md#fair-play-and-identity-what-is-and-isnt-protected)).
- A premier draft table runs on its first player's PC: if that player quits, the others' draft ends (the client asks before quitting).
