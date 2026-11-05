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
2. **Real client recipes (scripted clicks + screenshots):** `powershell -NoProfile -File tools\verify.ps1 -Only <rig,...>`; recipes are the
   table in `tools\verify.ps1` (`Rig`, `Deck`, `Clicks`). See the recipe table in `tools\verify.ps1`.

## Rigs

| Rig | Setup | Used for |
|---|---|---|
| `sami` (deck `sami`) | Me: 3 Plains + 3 Ornithopter + Sami, Wildcat Captain in play, Wurmcoil Engine {6} in hand. AI: passive Mountains | Granted affinity (`AffectedZone$ Stack` statics) must make the hand spell castable and shown at {3}. SelfPlay asserts the shown cost is 3 |
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
