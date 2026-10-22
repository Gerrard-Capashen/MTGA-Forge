# Patch distribution

What we hand to another user, where everything lives on their machine, and what must never be in the package.
Built by `tools\package.ps1`, checked by `tools\check-package.ps1` (the build fails when the check does).

## Package layout (`dist\MTGA-Forge\`, about 170 MB, 39k files, most of them Forge card scripts)

| Path | Content |
|------|---------|
| `MTGA-Forge.exe` (+ `.config`) | the one start button: the launcher window (WinForms, .NET Framework 4.8, source `bridge\Launcher`). First start: explains and runs the patch with step progress; then your name (stored in the profile: the box is prefilled from `data\profile.json` and shows the player id and the `#tag` the game adds) and an optional friend code (Casual Play pairs online through the matchmaking coordinator, see `coordinator/`; the code restricts the pairing to players who typed the same one) plus an Offline switch, with live queue/match status read from the server log. It only collects settings and runs the two scripts hidden |
| `MTGA-Forge.cmd` | command-line start: no arguments opens the exe, with arguments it runs `tools\launcher.ps1` directly |
| `tools\` | `launcher.ps1`, `forge-patch.ps1`, `layout.ps1`, `verify.ps1`, `check-package.ps1` |
| `patcher\Patcher.exe` + `Mono.Cecil*.dll` | Cecil patcher on .NET Framework 4.8 (part of Windows 10/11: no SDK or runtime to install). Verbs: (none) patch Core.dll + AK dll, `identity`, `unityconnect`, `check-bridge`, `audit-bridge` |
| `patcher\ForgeBridge.dll` | our client code, prebuilt (netstandard2.1), contains only namespace `ForgeBridge` |
| `runtime\` | jlink JRE 21 (60 MB, 15 modules), `runtime\bin\java.exe` |
| `server\lib\` | `forge-core/game/ai/gui/gui-mtga` jars + 75 third-party jars (no Maven, no source checkout) |
| `server\forge-gui\res\` | Forge rules data: card scripts, editions, formats, AI profiles, `languages\en-US.properties`. No adventure mode, images, music, sounds, skins, fonts, quest/conquest data |
| `dist.json` | marker for "packaged" mode (`tools\layout.ps1` switches paths on it) + commit and build time |

Nothing is on PATH or installed: only Windows PowerShell 5.1 and `robocopy` (both ship with Windows) are used.

## Where things live on the user's machine (`-Folder`, default `%LOCALAPPDATA%\MTGA-Forge`, remembered in `config.json` there)

| Path | Content | Created by |
|------|---------|-----------|
| `<Folder>\client\` | patched copy of the user's own install (about 14 GB) incl. `forge-patch\state.json`, `forge-patch\carddb\` (pristine manifest + DB of that build, ~600 MB with the generated one) | patcher |
| `<Folder>\data\` | decks (Forge precons seeded on first start + the user's), **`profile.json`**: the player's profile (GUID player id made on the first start, name, equipped and owned cosmetics, preferences; replaces `preferences.json`, which is imported once; fields in `docs\profile.md`) | server |
| `<Folder>\logs\` | `server-<time>.log(.err)`, 20 newest | launcher |
| `%APPDATA%\Forge`, `%LOCALAPPDATA%\Forge\Cache\pics` | Forge profile and the on-demand Scryfall art cache (card art is fetched at run time from Scryfall, never shipped) | server |
| `%USERPROFILE%\AppData\LocalLow\Forge Bridge Project\MTGF` | the client's `Player.log` (own identity, the real MTGA's folder is untouched) | client |

Forge's `res\` is read from the package (working directory of the server = `server\forge-gui`); the package folder is never written to.

## Launcher flow (`tools\launcher.ps1`)

1. Settings (`-Folder`, `-Source` remembered), one instance per folder (named mutex).
2. `forge-patch.ps1 -Check`: compares the install's fingerprint (Steam build id + main manifest name + Core.dll SHA-256 + MTGA.exe size) with `client\forge-patch\state.json` and that every step finished. Exit 0 current, 3 needs patching, 4 install not found (starts the existing client if there is one), 5 Steam is mid-update (`StateFlags` != 4: refuses, the files are not one build yet).
3. Needs patching (first start, MTGA update, `-Repatch`): disk-space check (install size + 2 GB), then `forge-patch.ps1` (refresh, orig, identity, unityconnect, storeconfig, bridge + check-bridge, patch, carddb, manifest; about 1.5 min on an SSD). A failed step stops everything: the client is not started, the original game is untouched, `state.json` keeps the failing step.
4. Starts `runtime\bin\java.exe ... forge.mtga.ForgeMtgaMain`, hidden, log to `logs\`; refuses if a port is taken by something else, kills a leftover server of this package; waits (listening sockets of that pid, no probing connect) until 30003 / 30010 / 30011 listen (max 3 min).
5. Starts `client\MTGA.exe`, watches both; the server is stopped (process tree) when the client exits, when the launcher is closed, or the client is closed if the server dies.
6. `-Firewall` also creates the outbound-block rule (one UAC prompt); off by default.

## NOT allowed in the package

1. Anything of Wizards' game: `MTGA.exe`, `UnityPlayer.dll`, `Core.dll` and every other file of `MTGA_Data\` (`Managed\*.dll`, `globalgamemanagers`, `*.assets`, `*.resS`, `level*`, `sharedassets*`, `boot.config`, `StreamingAssets`, `Downloads\` incl. manifests `*.mtga`, `Raw_CardDatabase`, asset bundles), Wwise `*.bank/*.wem`, and our patched or `*.orig` copies of them. The patcher produces all of this on the user's machine from their own install.
2. Any byte-identical copy of a file of the user's install under another name (hash comparison against the install).
3. Decompiled Wizards code or anything derived from it by tooling: `docs\decompiled\`, descriptor dumps, the original mock server (`tools\mock`), capture logs, Player.log excerpts.
4. Card art or any images, audio or video: no `.jpg .png .gif .bmp .webp .tga .dds .psd .ico .svg .tif .ogg .mp3 .wav .flac .mp4 ...`, not even inside the jars (one allowed library exception: Jetty's own `favicon.ico`). That excludes Forge's UI skins, fonts, adventure mode sprites, sounds, music and `res\defaults` / `res\effects` pictures. Card art comes from Scryfall at run time, art ids of cards the client lacks are served by our server.
4b. The cosmetic catalogue (avatar, sleeve, pet, emote and title ids): not shipped. `forge.mtga.profile.CosmeticCatalog` reads the ids at run time from the user's own install (`MTGA_Data\Downloads\ALT\*.mtga`, `Raw_ClientLocalization`), the same way the card DB comes from it; the package holds only that reader (`docs\profile.md` section 2).
5. The user's data or ours: decks, `profile.json`, `preferences.json`, `state.json`, Player.log, tokens, e-mail, absolute paths of the dev machine (`C:\Users\...`, `D:\Development`).
6. Binaries other than: `Patcher.exe`, `Mono.Cecil*.dll`, `ForgeBridge.dll` (no types outside `ForgeBridge`, no embedded resources: nothing copied from the game), the jlink runtime, and the jars.
7. Top-level entries other than `MTGA-Forge.cmd README.txt dist.json LICENSE-Forge.txt tools patcher runtime server`.

`check-package.ps1` enforces 1, 2 (by size, then SHA-256, against the auto-detected install), 4, 5, 6, 7 and the folder/file-name part of 1/3; it exits 1 on any violation.

### Open decisions (for the owner, not the script)

- **Protocol definitions.** `forge-gui-mtga.jar` carries `Wizards.Arena.*.proto` and the generated classes (`com/wizards/mtga`): the message formats our server must speak, reconstructed from the client's descriptors. They are interface descriptions, not game assets, and the check lists them as a NOTE, but they are derived from Wizards' client: decide before publishing.
- **The git repository is not the package.** The repo itself contains `docs\decompiled` (decompiled Wizards code), `tools\mock\*` and capture-derived notes. Never publish the repo as the patch distribution; if the repo is ever made public, remove those first.
- **Forge is GPL.** The package ships Forge's jars and card scripts (`LICENSE-Forge.txt`); our changes live in the `forge-gui-mtga` module and the `mtga` branch of the Forge clone, so the matching source has to be offered with any distribution.
- **Trademarks / terms.** The README says the project is unofficial; whether patching the client is acceptable under the Arena EULA is the owner's call, the package only avoids shipping any Wizards content.
- **Scryfall.** Art is fetched live (Forge's ImageFetcher); fine for personal use, a published launcher should respect their API rules (user agent, rate limit).

## Updating MTGA / this package

- MTGA update: nothing to do; the launcher sees the new fingerprint and patches again. All 9 Cecil hooks plus `check-bridge` are loud failures (patching stops, nothing starts). Breaks the patch cannot see (reflection by name in ForgeBridge, e.g. `Bootstrap.PrepareLoginAssets` going from `IEnumerator` to `void` in build 25919252) are found only by `verify.ps1`; run it after every new MTGA build on the developer machine and ship a new package if it fails.
- `check-bridge` replaces the compile check of the dev patcher: the prebuilt `ForgeBridge.dll` is checked member by member against the user's Managed folder.
- New package version: user unzips over the old folder; the client copy and data are elsewhere and survive.
