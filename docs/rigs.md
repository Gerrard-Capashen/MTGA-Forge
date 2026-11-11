# Rigs: deterministic test matches

A **rig** is a scripted, deterministic opening setup for a match (cards forced into play / hand, fixed decks), used to
reproduce a bug or regression-test a feature without playing to it by chance. Rigs live in the Java server (`forge/forge-gui-mtga`).

## How a rig works

- Selected with the JVM property `-Dmtga.rig=<name>` (env `MTGA_RIG=<name>` for the dev launcher). `-Dmtga.deck=<name>` picks the human deck;
  `-Dmtga.norig=1` disables rigs.
- **Decks:** `ForgeMtgaMain.decks(deck, rig)` returns `[human deck, AI deck]` for a deck name / rig (default: green creature deck vs red burn deck;
  most rigs swap in a passive opponent).
- **Board / hand:** `ForgeBackend.rigHand(Player)` runs when the human is set up. It moves cards by name with `rigAdd(player, "Card Name", zone, loyalty)`
  (creates the card, so it does not depend on the deck) or pulls them from the library. Instance ids are printed as `[mtga] rig: <card> instance <id>`.
- Note: the default `playtest` deck REPLACES a rig's deck, so build rig cards with `rigAdd` by name, not from the library.
- A rig hand does not survive a mulligan (hand is shuffled back): keep the opening hand.

## Running a rig

1. **Headless protocol match (SelfPlay, no client, fastest).** It is a fake client that plays a full game against the server and flags protocol problems.
   Build once, then:
   ```
   cd forge
   ..\tools\apache-maven-3.9.9\bin\mvn -o -B -q -DskipTests -pl forge-gui-mtga -am compile
   cd forge-gui
   java -Dmtga.rig=<rig> -Dmtga.deck=<deck> -Dselfplay.ai=plains -cp "$(cat ../../tools/cp.txt);../forge-gui-mtga/target/classes" forge.mtga.SelfPlay . ../../client <games> <seed>
   ```
   (Git Bash syntax; `tools/cp.txt` holds the classpath). The last line is `SELFPLAY OK` or `SELFPLAY FAILED`. Per-rig assertions live in
   `SelfPlay.java` (search `"sami".equals(...)`), plain `PROBLEM` lines are generic protocol checks. Mulligans are skipped for rigs that assert on the hand.
2. **Real client, by hand:** `powershell -NoProfile -File tools\play.ps1 -Duel -Rig <rig>` (`-Duel` goes straight into the rigged match; without it the client boots to the main menu and the rig is not used).
3. **Real client recipes (scripted clicks + screenshots):** `powershell -NoProfile -File tools\verify.ps1 -Only <rig,...>`; recipes are the
   table in `tools\verify.ps1` (`Rig`, `Deck`, `Clicks`). See the recipe table in `tools\verify.ps1`.

## Rigs

| Rig | Setup | Used for |
|---|---|---|
| `sami` (deck `sami`) | Me: 3 Plains + 3 Ornithopter + Sami, Wildcat Captain in play, Wurmcoil Engine {6} in hand. AI: passive Mountains | Granted affinity (`AffectedZone$ Stack` statics) must make the hand spell castable and shown at {3}. SelfPlay asserts the shown cost is 3 |
| `mill` (deck `mill`) | Me: 5 Islands + 3 Swamps in play, Doomsday + Traumatize in hand. AI: passive Mountains | Mass moves out of a library: Doomsday exiles it, Traumatize mills half the target's. Only the first `-Dmtga.massmove` (6) cards of each move get a ZoneTransfer animation. SelfPlay asserts one big diff with a capped number of Mill transfers |
| `phasing` | Me: 2 Islands + 2 Swamps in play, Kaito Shizuki in hand. AI: passive Mountains | Phasing: Kaito phases out at the end step of the turn it entered and back in at the next untap. The same object moves to the shared `PhasedOut` zone (23) and back, with `PhasedOut` / `PhasedIn` annotations; the client keeps drawing it on the battlefield, faded. SelfPlay asserts both annotations were sent |
| `room` | Me: 6 Plains in play, Surgical Suite (Room) in hand, Grizzly Bears in graveyard. AI: passive Mountains | A Room in hand is offered as `CastLeftRoom` + `CastRightRoom` (the client shows the choice); the unlocked-later door is a `Special` action with ability 349 (left) / 350 (right). SelfPlay casts the right half, then unlocks the left, and asserts both were offered |
| `arrow` | Me: 3 Mountain + 3 Island + Prodigal Sorcerer in play, Lightning Bolt, Shock, Sleep of the Dead in hand. AI: Grizzly Bears x2, Hill Giant | Target arrows: a spell or ability with a target on the stack carries a persistent `TargetSpec` annotation (the client draws the arrow from it). Cast a spell or tap the Sorcerer at a creature or a player and check the arrow stays while it is on the stack. Scripted check of the arc: recipe `bolt-arrow` |
| `ghast` | Me: 4 Swamps, 2 Shambling Ghast, Carrion Feeder (sacrifice a creature, free) and Bloodthrone Vampire in play. AI: passive Mountains, 2 Grizzly Bears, Hill Giant, Mortician Beetle | Modal dies trigger: sacrifice a Ghast, pick "-1/-1" and the client must ask for the target (an opponent's creature); "Treasure" needs none. Play by hand with `play.ps1 -Duel -Rig ghast` |
| `addcost` | Me: 3 Swamps in play, Bitter Triumph + Iridescent Vinelasher + 2 Grizzly Bears in hand. AI: passive Mountains, a Grizzly Bears | Additional costs the player must decide: Bitter Triumph's "discard a card or pay 3 life" is a `ChooseOrCost` casting-time option (a SelectN over prompt-id parameters: 1024 Discard a card., 4275 Pay 3 life.); Vinelasher's Offspring {2} an `AdditionalCost` option naming the card's Offspring ability, with Done. SelfPlay asserts both were asked. Play by hand with `play.ps1 -Duel -Rig addcost` |
| `sprawl` | Me: 3 Forests in play, Utopia Sprawl, Wild Growth, Lightning Bolt and Hill Giant in hand. AI: passive Mountains | Mana-enhancing Auras (`TapsForMana` triggers, not part of the land's own ability). Cast Utopia Sprawl on a Forest and choose red: the Aura shows the chosen color, Lightning Bolt ({R}) is castable and auto-tapped from the enchanted Forest, and so is Hill Giant ({3}{R}) with both Auras on one Forest (5 mana). SelfPlay asserts the chosen-color `LinkInfo` is published. Play by hand with `play.ps1 -Duel -Rig sprawl` |
| `gollum` | Me: 3 Swamps in play, Gollum, Riddle Master in hand. AI: passive Mountains | "Choose odd or even" as it enters: the client must show the Even/Odd parity buttons (a `Static`/`Parities` SelectNReq, `NonRepeatable`), and the chosen quality must stick. Play by hand with `play.ps1 -Duel -Rig gollum` |
| `venture` | Me: 8 Forests + Nadaar, Selfless Paladin in play, Avenging Hunter in hand. AI: passive Mountains | Dungeons: attack with Nadaar first and the client must show its dungeon browser (the three AFR dungeons, `IdType_CardGrpId`); the next venture shows the room picker on the dungeon map (needs the `DungeonStatus` annotation). Casting Avenging Hunter instead takes the initiative into Undercity, our generated card (no map frame), whose rooms come up in the generic ability browser. Play by hand with `play.ps1 -Duel -Rig venture` |
| `dv` | Wastes x7 in play, Devourer of Destiny in hand, AI Grizzly Bears | cast trigger targeting |
| `ascend` | 9 Plains, Ocelot Pride, two 99-loyalty planeswalkers | Ascend + emblems |
| `menace`, `block`, `bf`, `bt`, `bolt` | creatures in play / burn deck, passive or attacking AI | combat, blocking, targeting |
| `scepter`, `evoke`, `passage`, `color`, `bos` | specific cards in hand/play | spell-by-effect, evoke, land choices, Boseiju/Cavern |
| `nc`, `nf`, `et`, `sire`, `fc`, `nyk`, `pt*` | see `rigHand` / `ForgeMtgaMain.decks` comments | CardDbBuilder cards, Eldrazi, restricted mana, Full Control, Nykthos, playtest |

## Adding a rig

1. `ForgeMtgaMain.decks`: add an `if ("<rig>".equals(rig))` block setting the decks.
2. `ForgeBackend.rigHand`: add the board (`rigAdd`), end with `return;`.
3. Optional assertion in `SelfPlay.java` (see the `sami` check in the `ActionsAvailableReq` case and the end-of-game check) and a recipe in `tools\verify.ps1`.
4. Add a row to the table above.
