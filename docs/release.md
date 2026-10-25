# Releasing an update

How a new MTGA Forge version reaches players, and the steps to publish one. Last done: v2026.10.25-1823.

## How updates work

- The website (`site/`, Worker `play` at https://mtgaforge.com, also https://play.mtga-forge.workers.dev for launchers built before the domain) serves `site/public/latest.json`: release time, commit, zip SHA-256, size, download URL, notes, VirusTotal results. The page's download button, SHA and scan lines all come from it, so a release never edits `index.html` (only feature text does).
- The launcher (`bridge/Launcher/UpdateChecker.cs`) compares its own `dist.json` `builtAt` (written by `tools/package.ps1`) with `latest.json` `released` once per start. If the release is newer, it shows the "A newer MTGA Forge is available" banner with a link. Nothing downloads automatically. A dev copy (no `dist.json`) never checks.
- The zip is hosted as a GitHub release asset on `Gerrard-Capashen/MTGA-Forge`.
- The coordinator (`coordinator/`, Worker `mm`) is deployed on its own. It must be deployed before a release that needs new endpoints or DOs (old clients keep working against it).

## Before

1. Commit everything in both repos (main repo and `forge/`). The package records the main repo's commit and warns about a dirty tree.
2. Coordinator changed? `cd coordinator; npm run dev` + `npm test` (all suites), then `npm run deploy`. New DO classes need a `[[migrations]]` tag in `wrangler.toml`; new D1 migrations: `npm run db:migrate`.
3. Headless checks: `powershell -NoProfile -File tools\verify.ps1 -SkipClient` (all SelfPlay / account / social / queue recipes; online ones use the live coordinator). Premier drafts: `forge.mtga.fd.PremierCheck <client>` and, with `-Dmtga.premier.readySecs=4`, `forge.mtga.fd.PremierOnlineCheck <client> wss://mm.mtga-forge.workers.dev` (a few minutes: real table forming times).
4. Launcher UI changed? Build it and look at `MTGA-Forge.exe --shots <folder>`.

## Release (one command)

```
powershell -NoProfile -File tools\release.ps1 -Build
powershell -NoProfile -File tools\release.ps1 -Scan -Upload github -Deploy -Notes 'what changed'
```

- `-Build`: `package.ps1 -BuildServer -Zip` (~8 min) incl. `check-package` (must say CHECK OK; the note about Wizards-named protocol definitions in the server jar is known and accepted).
- `-Scan`: VirusTotal (`vt` CLI, key from `vt init` / `%USERPROFILE%\.vt.toml`); stops on any malicious hit unless `-AllowDetections`. Results go to `dist\MTGA-Forge.zip.scan.json` and onto the site.
- `-Upload github`: `gh release create v<builtAt>` with the token in `%LOCALAPPDATA%\MTGA-Forge-release\token.txt` (fine-grained, owner Gerrard-Capashen, Contents read/write; the script refuses a token of another account).
- `-Deploy`: writes `site/public/latest.json` and deploys the site.
- Between the two commands, smoke-test the package itself: `cd dist\MTGA-Forge; powershell -NoProfile -File tools\verify.ps1 -SkipClient -Client <dev client> -Only sp-default,sp-queues`.

## After

1. Check live: `latest.json` has the new commit/SHA, the page shows the new text, a download of the URL hashes to the published SHA.
2. Commit the new `site/public/latest.json`.
3. Start an older launcher (e.g. the previous zip unpacked in Downloads): the update banner must appear. Launchers up to 2026-10-24 time out after 6 s on a cold start and may show it only on a second start (fixed after that release).

## Gotchas

- PowerShell 5.1: `git commit -F -` with a here-string does not work; write the message to a file and use `-F <file>`. `Set-Content -Encoding utf8` adds a BOM: write with `[IO.File]::WriteAllText(..., New-Object Text.UTF8Encoding $false)`.
- `wrangler dev` sometimes drops `--var` overrides after a hot reload: restart it before running tests that depend on them.
- VirusTotal makes uploaded files public; the package contains no personal data.
