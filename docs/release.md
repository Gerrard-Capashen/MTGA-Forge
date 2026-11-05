# Releasing an update

How a new MTGA Forge version reaches players, and the steps to publish one. Last done: v2026.11.04-1508.

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
  Known and accepted (user, 2026-11-05): a couple of machine-learning / heuristic false positives (engine results like `ML`, `AI`, `Heuristic`, `Generic`) on every build. These need no confirmation: rerun with `-AllowDetections` and list the engines in the release report. Stop and ask only when a detection is a named signature or clearly more than the usual two.
- `-Upload github`: `gh release create v<builtAt>` with the token in `%LOCALAPPDATA%\MTGA-Forge-release\token.txt` (fine-grained, owner Gerrard-Capashen, Contents read/write; the script refuses a token of another account).
- `-Deploy`: writes `site/public/latest.json` and deploys the site.
- Publishing the GitHub release triggers `.github/workflows/discord-release.yml`, which posts the release notes to Discord (pre-releases are skipped; re-post with the workflow's manual run and a tag).
- Release notes: short player-facing bullets (`- New: ...`, `- Fix: ...`), from the commits since the last release in BOTH repos (`git log <last release commit>..HEAD` here, `git -C forge log --since <last release time>`). Leave out internal-only changes (PROGRESS.md, dev scripts) and anything PROGRESS marks unverified unless it was tested.
- Between the two commands, smoke-test the package itself: `cd dist\MTGA-Forge; powershell -NoProfile -File tools\verify.ps1 -SkipClient -Client <dev client> -Only sp-default,sp-queues`.

## After

1. Check live: `latest.json` has the new commit/SHA, the page shows the new text, a download of the URL hashes to the published SHA.
2. Commit the new `site/public/latest.json`.
3. Start an older launcher (e.g. the previous zip unpacked in Downloads): the update banner must appear. Launchers up to 2026-10-24 time out after 6 s on a cold start and may show it only on a second start (fixed after that release).

## Reusable prompt

Paste this into a new session to have Claude run a release:

> Make a new MTGA Forge release, following docs/release.md.
> 1. Check that both repos (this one and `forge/`) are committed; tell me about anything uncommitted before going on.
> 2. Find the last release (commit and time in `site/public/latest.json`) and list what changed since then in both repos.
> 3. Build and scan: `tools\release.ps1 -Build -Scan`. It must end with CHECK OK. The usual machine-learning / heuristic false positives (about two engines) are accepted: go on with `-AllowDetections` and list them in your report. Wait for my call only on a named signature or more detections than usual.
> 4. Smoke-test the package: `tools\verify.ps1 -SkipClient -Client <a patched client folder> -Only sp-default,sp-queues` from `dist\MTGA-Forge`.
> 5. Draft the release notes (player-facing bullets) and show them to me with the scan results. Publish only after I confirm: `tools\release.ps1 -Upload github -Deploy -Notes '<notes>'` (add `-AllowDetections` when the scan had only the accepted false positives).
> 6. Check live: mtgaforge.com/latest.json has the new commit and SHA, and the download URL works. Then commit `site/public/latest.json` as "Release <tag>: latest.json and notes" and update "Last done" in docs/release.md.

## Gotchas

- PowerShell 5.1: `git commit -F -` with a here-string does not work; write the message to a file and use `-F <file>`. `Set-Content -Encoding utf8` adds a BOM: write with `[IO.File]::WriteAllText(..., New-Object Text.UTF8Encoding $false)`.
- `wrangler dev` sometimes drops `--var` overrides after a hot reload: restart it before running tests that depend on them.
- VirusTotal makes uploaded files public; the package contains no personal data.
