# Notes for AI coding agents

MTGA Forge runs MTG Arena's client against the Forge rules engine. Read [CONTRIBUTING.md](CONTRIBUTING.md) for the full setup. This file holds the rules and commands you need while working.

## Hard rules

- **No Wizards material in git.** `docs/decompiled/`, `docs/proto/`, `forge/forge-gui-mtga/src/main/proto/` and `tools/mock/gen/` are generated locally from the developer's own MTGA install and are gitignored. Never commit them, never force-add them, never quote their code in commits, PRs or issues.
- **Read the decompiled client, don't copy it.** Use it to learn class and method names, message flows and behaviour. Write our own implementation; never paste method bodies into `bridge/` or `forge-gui-mtga`.
- **Never modify the user's MTGA install.** Only the patched copy (`client/`, `client-fresh/` or a launcher folder) is ever written.
- **No personal data** in code, docs or logs you commit: no `C:\Users\<name>` paths, emails, IPs or machine names. `tools/check-package.ps1` enforces this for the release package.
- **No AI co-author trailers** in commit messages.

## Layout

- `bridge/Patcher` (C#, net48, Mono.Cecil): patches `Core.dll` of the copy. `bridge/ForgeBridge` (C#, netstandard2.1): our code inside the client. `bridge/Launcher` (WPF): `MTGA-Forge.exe`.
- `forge/` is a separate git repo (the Forge fork, branch `mtga`). `forge/forge-gui-mtga` is the game server (Java 17): Front Door (lobby) and Match Door (GRE game protocol).
- `coordinator/` (TypeScript, Cloudflare Worker + Durable Objects + D1): accounts, friends, matchmaking, tables, ranks, mastery, quests.
- `site/`: mtgaforge.com. `tools/`: build, patch, test, package, release scripts (PowerShell 5.1).
- `docs/architecture.md`: how the pieces fit, online play and the trust model. Read it first.
- `docs/development.md`: how the client consumes game state, the card database, MTGA updates, testing. `docs/`: protocol, profile, rigs, distribution rules.

## Commands

```powershell
tools\gen-protos.ps1 [-ManagedDir <MTGA_Data\Managed>] [-Docs]   # protocol schemas from the local client
tools\decompile.ps1 [-Assemblies Core,SharedClientCore]            # decompiled reference into docs\decompiled
tools\forge-patch.ps1 -Target client [-BuildServer]                # (re)build the patched copy
tools\play.ps1                                                     # build + start server and client
tools\verify.ps1 -SkipClient [-Only <recipe>]                      # headless checks
cd coordinator; npm run dev; npm test                              # coordinator locally
```

Server build: `cd forge; ..\tools\apache-maven-3.9.9\bin\mvn.cmd -B -q -o -DskipTests -pl forge-gui-mtga -am compile`.

## Working style

- Read `docs/development.md` and the relevant `docs/` notes before changing a feature; add a finding there when you learn something non-obvious about the client.
- Check behaviour against the decompiled client rather than guessing what it expects.
- Prove changes with a `verify.ps1` recipe or a test rig (`docs/rigs.md`), not just a compile.
- PowerShell 5.1 is the target: no `&&`, `??` or ternaries in scripts.
