MTGA-Forge: play Magic Arena's client against the Forge rules engine (Bot Play offline, Casual Play online).

WHAT YOU NEED
- Windows 10/11 and MTGA installed (Steam). Nothing else: Java and the patch tools are in this folder.
- About 15 GB of free disk space for the patched copy of the game, plus ~1 GB for the card database built on first start.

START
  Double-click MTGA-Forge.exe. The window walks you through four steps:
    1. It finds your MTGA install (Steam libraries on any drive), or you press Browse and pick the folder.
    2. You choose where the patched copy goes (default C:\MTGA Forge; any folder you can write to works, but not Program Files).
    3. It prepares the game: copies it, patches it, builds the card database from YOUR install.
    4. You pick how to play and press Start.
  Preparing takes several minutes the first time and again after an MTGA update. After that, Start takes a few seconds: the server
  starts, then the game; closing the game stops the server. "Details" at the bottom shows every command it runs and what it prints
  (the window grows to fit it; "Copy log" is handy if something goes wrong).
  You never need a command prompt.

PLAY ONLINE (each player with their own MTGA and this package, the same MTGA version)
  In the game: Play > Find Match > Casual Play > Standard, Pioneer or Modern, choose a deck of that format, Play. You are
  paired with another player waiting in the same format; the one who waited longest runs the game on their PC. Nothing to
  set up: no addresses, no router ports. Only the pairing and the game messages go through a small matchmaking server;
  no deck lists or accounts are stored there.
  To play a specific friend: both type the same Friend code in the window (like blue-otter-7) before pressing Start, then both
  pick the same Casual Play format. Without a code you meet random players.
  Friends: the game's own Friends list (bottom left corner) works: add a friend by typing their Name#12345 (the number is part of
  the name: ask them), they see your request and accept; you see each other online, busy or offline.
  Challenges: invite a friend to a challenge from the Friends panel; the deck type spinner shows Standard, Pioneer, Modern and 40 cards.
  Best of one only for now; the player who creates the challenge hosts the game on their PC.
  If you are told "different card database", one of you has another MTGA version or another MTGA-Forge package.
  "Offline" in the window (or -Offline) switches online play off.
  Your name: the window suggests a random one the first time; the game shows it as Name#12345 (the number is given by the
  matchmaking server so that no two players have the same Name#number). Your identity is a key file in <Folder>\data
  (identity.json): use "Back up identity..." in the window to keep a copy, "Restore..." to get your name back on another PC.
  (Single player: Play > Find Match > Bot Play. Each queue only accepts decks legal in its format.)

  Settings in the window: your name, a friend code, Offline, "Back up identity" / "Restore", and a switch to block the patched
  game from the internet with a Windows Firewall rule (asks for administrator once; only affects the patched MTGA.exe, not the
  Forge server or matchmaking; it is for those who want to be 100% sure the game itself never contacts anything).

WHAT IT DOES TO YOUR GAME
  Nothing. Your Steam install is only read. The patched game is a separate copy in <Folder>\client; it uses its own
  settings, never signs in to MTGA's servers and sends nothing to them (the patch switches off its network calls; the optional
  firewall rule blocks it as well). Online play only talks to the matchmaking server, from the Forge server process. Card art of cards that MTGA does not have is fetched from Scryfall on demand.

UPDATES
  MTGA-Forge: when the window opens it asks our website for the latest release (one plain request, nothing about you is sent). If
  yours is older, a banner links to the new download. Nothing is downloaded or installed automatically.
  After an MTGA update: the launcher notices and prepares again before starting. If a new MTGA build changed something the patch
  depends on, patching stops with an error and nothing is started; you need a newer MTGA-Forge then. Your decks stay
  (<Folder>\data).

FILES
  <Folder>\client     the patched game copy (can be deleted any time; it is rebuilt)
  <Folder>\data       your decks and preferences, the card database snapshot, your identity
  <Folder>\logs       server logs (the 20 newest)
  The game's own log: %USERPROFILE%\AppData\LocalLow\Forge Bridge Project\MTGF\Player.log

UNINSTALL
  Press "Uninstall..." in the window: tick what to remove (the game copy, logs, your decks and identity, the game's saved
  preferences, the launcher's settings). Nothing outside those is touched, and your Steam MTGA is never modified.

COMMAND LINE (optional, for scripting)
  MTGA-Forge.cmd -Folder D:\MTGA-Forge -Source "D:\Games\MTGA" -PatchOnly -Repatch -FriendCode blue-otter-7 -Offline -Firewall
  Without -Folder the command-line start uses %LOCALAPPDATA%\MTGA-Forge; the window uses the folder you chose.

THIS PACKAGE CONTAINS
  Our code (patcher, bridge, server), Forge's rules engine and card scripts (GPL), a Java runtime. It contains no file of
  Wizards of the Coast and no card art: everything of the game is taken from your own install on your own machine.
  Magic: The Gathering and MTG Arena are trademarks of Wizards of the Coast; this project is unofficial.
  Forge: https://github.com/Card-Forge/forge  (license in LICENSE-Forge.txt)