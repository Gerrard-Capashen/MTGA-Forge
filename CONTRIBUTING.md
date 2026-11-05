# Contributing to MTGA Forge

Thanks for helping. This page covers the one hard rule, how to set up a dev machine, how the code is laid out and how to send a change.

## The hard rule: no Wizards material

MTGA Forge patches a player's own copy of MTG Arena. It must never ship or publish Wizards of the Coast's code, art or data.

- Never commit decompiled code, `.proto` schemas, client DLLs, asset bundles, card databases, art, audio or Player.log files. The `.gitignore` already covers the generated folders: don't force-add them.
- Never paste decompiled code into issues, PRs or chat. Describe what the client does in your own words. Class, method and field names are fine.
- Bug report with a log? Run it through `tools\redact-log.ps1` first (it blanks your user name, computer name and IPs).

PRs that break this rule are closed.

## What you need

- Windows 10 or 11 and MTG Arena (Steam or the Wizards installer), fully downloaded. Sign in once so the game fetches its files.
- JDK 21 (Forge targets Java 17)
- Maven 3.9: unzip it into `tools\apache-maven-3.9.9\` (the scripts look there)
- .NET SDK 8 or newer (builds the patcher, the bridge and the launcher)
- Python 3 with `pip install protobuf` (generates the protocol schemas)
- Node.js 20+ (only for the coordinator or the website)
- Optional: `dotnet tool install -g ilspycmd`, for the decompiled reference

## First-time setup

Run these from PowerShell. Replace `<MTGA>` with your install folder, e.g. `C:\Program Files (x86)\Steam\steamapps\common\MTGA`.

```powershell
git clone https://github.com/Gerrard-Capashen/MTGA-Forge.git
cd MTGA-Forge
git clone -b mtga https://github.com/Gerrard-Capashen/forge.git forge

# 1. Protocol schemas from your own install (read only) into forge\forge-gui-mtga\src\main\proto
powershell -NoProfile -File tools\gen-protos.ps1 -ManagedDir "<MTGA>\MTGA_Data\Managed"

# 2. First server build (online: downloads Forge's dependencies) + the classpath file the scripts use
cd forge
..\tools\apache-maven-3.9.9\bin\mvn.cmd -B -DskipTests -pl forge-gui-mtga -am compile dependency:build-classpath "-Dmdep.outputFile=$PWD\..\tools\cp.txt"
cd ..

# 3. A patched copy of your install in .\client (your install is never changed; the first copy takes a while, ~15 GB)
powershell -NoProfile -File tools\forge-patch.ps1 -Target client -Source "<MTGA>"

# 4. Play: starts the server and the patched client
powershell -NoProfile -File tools\play.ps1
```

After the first build the scripts build offline and only regenerate the schemas when they're missing. After an MTGA update, re-run `tools\forge-patch.ps1 -Target client` (it notices the new version) and `tools\gen-protos.ps1 -ManagedDir client\MTGA_Data\Managed`.

Optional reading aid: `tools\decompile.ps1` decompiles the client assemblies we hook into `docs\decompiled\`. It's local and gitignored: don't share it.

## Where things live

| Path | What |
|---|---|
| `bridge\Patcher` | patches `Core.dll` of the copy (Mono.Cecil) and redirects the client to the local server |
| `bridge\ForgeBridge` | our code inside the client: local Front Door, match plumbing, UI tweaks |
| `bridge\Launcher` | `MTGA-Forge.exe`, the player-facing launcher |
| `forge\forge-gui-mtga` (fork) | the game server: Front Door and Match Door protocol, decks, drafts, profile, AI matches |
| `coordinator\` | Cloudflare Worker: accounts, friends, matchmaking, ranks, quests (TypeScript) |
| `site\` | mtgaforge.com (static) |
| `tools\` | build, patch, test, package and release scripts |
| `docs\` | design notes and findings; `PROGRESS.md` is the running dev log |

Game-rule or server changes go to the Forge fork (PR against its `mtga` branch). Everything else goes here (PR against `main`).

## Testing

- Headless checks (no client window): `powershell -NoProfile -File tools\verify.ps1 -SkipClient`. Add `-Only <recipe>` to run one.
- In the client: `tools\play.ps1`. Test rigs for specific board states are listed in `docs\rigs.md`.
- Coordinator: `cd coordinator; npm install; npm run dev`, then `npm test` in a second window. It runs locally with a local database; only the maintainer deploys.

## Sending a change

1. Open an issue first for anything bigger than a bug fix, so we agree on the approach before you write it.
2. Keep a PR to one topic. Say what you changed and how you tested it (which `verify.ps1` recipes, what you played).
3. Commit messages: a short summary line in the imperative ("Fix X when Y"), details below it if needed.
4. Contributions are licensed under GPL-3.0, like the rest of the project.

## Talk to us

- Bugs and feature requests: GitHub issues. Include your MTGA Forge version and a redacted Player.log.
- Questions and coordination: the [Discord](https://discord.gg/xQSgHsEnQ).
