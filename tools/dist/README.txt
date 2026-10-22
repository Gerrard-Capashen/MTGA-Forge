MTGA-Forge: play Magic Arena's client against the Forge rules engine, offline.

WHAT YOU NEED
- Windows 10/11 and MTGA installed (Steam). Nothing else: Java and the patch tools are in this folder.
- About 15 GB of free disk space for the patched copy of the game, plus ~1 GB for the card database built on first start.

START
  Double-click MTGA-Forge.exe: a small window opens. On the first start it prepares your game (press the button, it
  explains the steps), then you pick how to play and press Start.
  First start (and after every MTGA update): the game is copied and patched, then the card database is generated from
  YOUR install. This takes several minutes. After that, start is a few seconds: the server starts, then the game; closing
  the game stops the server.

PLAY A FRIEND (two players, each with their own MTGA and this package, the same MTGA version)
  Both install Tailscale (free, tailscale.com) and sign in to the same network, or play on the same local network.
  Host:  pick "I host", Start. The window lists addresses: send your friend the Tailscale one (100.x.x.x:30012).
         In the game: Play > Find Match > Bot Match, choose a deck, Play. It waits for your friend.
  Guest: pick "I join", paste the address, Start, and once the host is waiting do the same in the game
         (Play > Find Match > Bot Match, deck, Play). The two of you then play a normal Magic game.
  The first time the host starts, Windows asks whether "Java" may accept connections: allow it (private networks).
  Without Tailscale, the host can instead open TCP port 30012 on their router. If you are told "different card
  database", one of you has another MTGA version or another MTGA-Forge package.
  (The queue entry still says "Bot Match": that is expected for now.)

  Options (run from a command prompt):
    MTGA-Forge.cmd -Folder D:\MTGA-Forge      where the patched copy, your decks and the logs go (default %LOCALAPPDATA%\MTGA-Forge)
    MTGA-Forge.cmd -Source "D:\Games\MTGA"    MTGA install to use when Steam is not where we look (remembered)
    MTGA-Forge.cmd -PatchOnly                 patch, do not start
    MTGA-Forge.cmd -Repatch                   patch again from scratch
    MTGA-Forge.cmd -P2P host                  (or: -P2P join -Peer <address>) a friend game without the window
    MTGA-Forge.cmd -Firewall                  also add a firewall rule that blocks the patched MTGA.exe from the internet (asks for admin once)

WHAT IT DOES TO YOUR GAME
  Nothing. Your Steam install is only read. The patched game is a separate copy in <Folder>\client; it uses its own
  settings, never signs in to Wizards and sends nothing to them (the patch switches off its network calls; the optional
  firewall rule blocks it as well). Card art of cards that MTGA does not have is fetched from Scryfall on demand.

AFTER AN MTGA UPDATE
  The launcher notices the new version and patches again before starting. If a new MTGA build changed something the patch
  depends on, patching stops with an error and nothing is started; you need a newer MTGA-Forge then. Your decks stay
  (<Folder>\data).

FILES
  <Folder>\client     the patched game copy (can be deleted any time; it is rebuilt)
  <Folder>\data       your decks and preferences, the card database snapshot
  <Folder>\logs       server logs (the 20 newest)
  The game's own log: %USERPROFILE%\AppData\LocalLow\Forge Bridge Project\MTGF\Player.log

THIS PACKAGE CONTAINS
  Our code (patcher, bridge, server), Forge's rules engine and card scripts (GPL), a Java runtime. It contains no file of
  Wizards of the Coast and no card art: everything of the game is taken from your own install on your own machine.
  Magic: The Gathering and MTG Arena are trademarks of Wizards of the Coast; this project is unofficial.
  Forge: https://github.com/Card-Forge/forge  (license in LICENSE-Forge.txt)