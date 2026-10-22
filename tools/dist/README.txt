MTGA-Forge: play Magic Arena's client against the Forge rules engine (Bot Play offline, Casual Play online).

WHAT YOU NEED
- Windows 10/11 and MTGA installed (Steam). Nothing else: Java and the patch tools are in this folder.
- About 15 GB of free disk space for the patched copy of the game, plus ~1 GB for the card database built on first start.

START
  Double-click MTGA-Forge.exe: a small window opens. On the first start it prepares your game (press the button, it
  explains the steps), then you pick how to play and press Start.
  First start (and after every MTGA update): the game is copied and patched, then the card database is generated from
  YOUR install. This takes several minutes. After that, start is a few seconds: the server starts, then the game; closing
  the game stops the server.

PLAY ONLINE (each player with their own MTGA and this package, the same MTGA version)
  In the game: Play > Find Match > Casual Play > Standard, Pioneer or Modern, choose a deck of that format, Play. You are
  paired with another player waiting in the same format; the one who waited longest runs the game on their PC. Nothing to
  set up: no addresses, no router ports. Only the pairing and the game messages go through a small matchmaking server (a free
  Cloudflare Worker); no deck lists or accounts are stored there.
  To play a specific friend: both type the same Friend code in the window (like blue-otter-7) before pressing Start, then both
  pick the same Casual Play format. Without a code you meet random players.
  Friends: the game's own Friends list (bottom left corner) works: add a friend by typing their Name#12345 (the number is part of
  the name: ask them), they see your request and accept; you see each other online, busy or offline.
  If you are told "different card database", one of you has another MTGA version or another MTGA-Forge package.
  "Offline" in the window (or -Offline) switches online play off.
  Your name: the window suggests a random one the first time; the game shows it as Name#12345 (the number is given by the
  matchmaking server so that no two players have the same Name#number). Your identity is a key file in <Folder>\data
  (identity.json): use "Back up identity..." in the window to keep a copy, "Restore..." to get your name back on another PC.
  (Single player: Play > Find Match > Bot Play. Each queue only accepts decks legal in its format.)

  Options (run from a command prompt):
    MTGA-Forge.cmd -Folder D:\MTGA-Forge      where the patched copy, your decks and the logs go (default %LOCALAPPDATA%\MTGA-Forge)
    MTGA-Forge.cmd -Source "D:\Games\MTGA"    MTGA install to use when Steam is not where we look (remembered)
    MTGA-Forge.cmd -PatchOnly                 patch, do not start
    MTGA-Forge.cmd -Repatch                   patch again from scratch
    MTGA-Forge.cmd -FriendCode blue-otter-7   only meet players with the same code (play a friend) without the window
    MTGA-Forge.cmd -Offline                   no online play
    MTGA-Forge.cmd -Firewall                  also add a firewall rule that blocks the patched MTGA.exe from the internet (asks for admin once)

WHAT IT DOES TO YOUR GAME
  Nothing. Your Steam install is only read. The patched game is a separate copy in <Folder>\client; it uses its own
  settings, never signs in to Wizards and sends nothing to them (the patch switches off its network calls; the optional
  firewall rule blocks it as well). Online play only talks to the matchmaking server, from the Forge server process. Card art of cards that MTGA does not have is fetched from Scryfall on demand.

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