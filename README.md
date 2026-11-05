# MTGA Forge

Play Standard, Pioneer, Modern and quick drafts in your own copy of MTG Arena, with the open source [Forge](https://github.com/Card-Forge/forge) rules engine running the game. Free cards, free cosmetics, friends and community matchmaking.

Unofficial fan project. Not made, endorsed or supported by the makers of MTGA or by Valve.

## Get it

Download from **https://mtgaforge.com** or from the [latest release](../../releases/latest). The site lists the SHA-256 and VirusTotal results for each release: check the file against them before you run it.

## Needs

- Windows 10 or 11
- MTG Arena (Steam version) on the same PC
- About 15 GB free disk space

Unzip and run `MTGA-Forge.exe`. It makes a patched copy of your install; your original is never changed. The launcher is not code-signed yet, so Windows SmartScreen may warn.

## Source

Everything is here, in two repositories:

| Repository | What |
|---|---|
| this one | launcher, client patcher and in-client bridge (C#), online coordinator and website (Cloudflare Workers), build and release scripts |
| [Gerrard-Capashen/forge](https://github.com/Gerrard-Capashen/forge), branch `mtga` | the Forge fork: the `forge-gui-mtga` module is the game server that speaks the client's protocol |

No Wizards of the Coast code, art or data is in either repository. Developers generate what they need (protocol schemas, a decompiled reference) from their own MTGA install. Want to help? Read [CONTRIBUTING.md](CONTRIBUTING.md).

## Why

Games get switched off as services. [Stop Killing Games](https://www.stopkillinggames.com/) argues players should be able to keep playing them. This project applies that idea to a game we like.

## License

GPL-3.0 (see [LICENSE](LICENSE)), the same as Forge.

---

Magic: The Gathering, MTG Arena and related names are trademarks of their respective owners. Rules engine: [Forge](https://github.com/Card-Forge/forge) (GPL). Card art comes from [Scryfall](https://scryfall.com/) and is never bundled. Provided as is, with no warranty.
