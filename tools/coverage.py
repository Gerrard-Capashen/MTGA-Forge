"""Card coverage: Forge card scripts vs the MTGA client card database.

Usage: python -I tools/coverage.py [repo_root] [--md docs/card-coverage.md]

Compares the distinct card names Forge implements (forge-gui/res/cardsfolder, the same name Forge's CardDb uses:
first face, or "A // B" for split cards) with the titles of the client's SQLite card database
(client/MTGA_Data/Downloads/Raw/Raw_CardDatabase_*.mtga, Cards.TitleId -> Localizations_enUS), and categorises the gaps.
"""
import glob
import os
import re
import sqlite3
import sys
import unicodedata
from collections import Counter, defaultdict

root = next((a for a in sys.argv[1:] if not a.startswith("--")), os.path.join(os.path.dirname(__file__), ".."))
md_out = sys.argv[sys.argv.index("--md") + 1] if "--md" in sys.argv else None
cards_dir = os.path.join(root, "forge", "forge-gui", "res", "cardsfolder")
db_path = glob.glob(os.path.join(root, "client", "MTGA_Data", "Downloads", "Raw", "Raw_CardDatabase_*.mtga"))[0]


def fold(s):
    """Loose key: no accents, case, punctuation or spacing differences."""
    s = unicodedata.normalize("NFKD", s).encode("ascii", "ignore").decode()
    return re.sub(r"[^a-z0-9/]+", "", s.lower())


# ---- Forge ------------------------------------------------------------------------------------------------------
forge = {}   # name -> {"types": str, "mode": str, "dir": str}
NON_DECK_TYPES = ("Plane", "Phenomenon", "Scheme", "Vanguard", "Conspiracy", "Dungeon")
for dirpath, _, files in os.walk(cards_dir):
    for f in files:
        if not f.endswith(".txt"):
            continue
        with open(os.path.join(dirpath, f), encoding="utf-8") as fh:
            text = fh.read()
        faces = text.split("\nALTERNATE\n")
        names = [re.search(r"^Name:(.*)$", face, re.M) for face in faces]
        names = [m.group(1).strip() for m in names if m]
        if not names:
            continue
        mode_m = re.search(r"^AlternateMode:(\w+)", text, re.M)
        mode = mode_m.group(1) if mode_m else ""
        name = " // ".join(names[:2]) if mode == "Split" else names[0]
        types_m = re.search(r"^Types:(.*)$", faces[0], re.M)
        forge[name] = {"types": types_m.group(1) if types_m else "", "mode": mode,
                       "dir": os.path.relpath(dirpath, cards_dir).split(os.sep)[0]}

# ---- MTGA card DB -----------------------------------------------------------------------------------------------
con = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
rows = con.execute("""
    select c.GrpId, coalesce(p.Loc, f.Loc), c.IsPrimaryCard, c.IsToken, c.IsRebalanced, c.IsDigitalOnly,
           c.ExpansionCode, c.DigitalReleaseSet, c.LinkedFaceType
    from Cards c
    left join Localizations_enUS p on p.LocId = c.TitleId and p.Formatted = 0
    left join Localizations_enUS f on f.LocId = c.TitleId and f.Formatted = 1""").fetchall()


def plain(title):
    if title is None:
        return None
    title = re.sub(r"<[^>]+>", "", title)
    return title.replace(" /// ", " // ")


mtga = defaultdict(list)   # primary non-token name -> rows
mtga_all = defaultdict(list)
for r in rows:
    name = plain(r[1])
    if name is None:
        continue
    mtga_all[name].append(r)
    if r[2] == 1 and r[3] == 0:
        mtga[name].append(r)

forge_fold = defaultdict(list)
for n in forge:
    forge_fold[fold(n)].append(n)

# ---- MTGA primary names vs Forge ---------------------------------------------------------------------------------
exact = {n for n in mtga if n in forge}
fold_only = {n: forge_fold[fold(n)] for n in mtga if n not in forge and fold(n) in forge_fold}
mtga_missing = sorted(n for n in mtga if n not in forge and n not in fold_only)


def mtga_category(n):
    r = mtga[n][0]
    if n.startswith("A-"):
        return "Alchemy rebalanced (A-)"
    if r[7]:
        return "digital-only set " + r[7].split("-")[0]
    if r[5]:
        return "digital-only card"
    return "paper card, no Forge script"


mtga_cats = Counter(mtga_category(n) for n in mtga_missing)

# what the old CardDb (raw join, any Formatted) would have named the primary split cards
split_titles = [n for n in mtga if " // " in n]

# ---- Forge names vs MTGA -----------------------------------------------------------------------------------------
mtga_folded = {fold(m) for m in mtga}
forge_on_arena = {n for n in forge if n in mtga or fold(n) in mtga_folded}
forge_nonprimary_only = {n for n in forge if n not in forge_on_arena and n in mtga_all}
forge_missing = [n for n in forge if n not in forge_on_arena and n not in forge_nonprimary_only]


def forge_category(n):
    info = forge[n]
    t = info["types"]
    if info["dir"] == "upcoming":
        return "upcoming (unreleased) set"
    if info["dir"] == "rebalanced":
        return "Alchemy rebalanced not on this client"
    if any(x in t for x in NON_DECK_TYPES):
        return "non-deck card (plane/scheme/vanguard/conspiracy/dungeon)"
    if re.search(r"\b(Attraction|Contraption)\b", t):
        return "Un-set Attraction/Contraption"
    return "regular card not on Arena"


forge_cats = Counter(forge_category(n) for n in forge_missing)

lines = []
out = lines.append
out("# Card coverage: Forge scripts vs MTGA client card database")
out("")
out("Generated by `python -I tools/coverage.py --md docs/card-coverage.md` (client DB `%s`)." % os.path.basename(db_path))
out("")
out("Names: Forge = first face, `A // B` for split cards (Forge CardDb naming). MTGA = `Cards.TitleId` ->"
    " `Localizations_enUS` with `Formatted=0` (plain text; `Formatted=1` carries rich text such as the Alchemy sprite),"
    " split titles `A /// B` rewritten to `A // B`.")
out("")
out("What the Front Door actually grants and how formats come out is measured with Forge's own lookup by"
    " `forge.mtga.CardCoverage` (see PROGRESS.md, \"Card pool and formats\").")
out("")
out("## Totals")
out("| | count |")
out("|---|---|")
out("| Forge distinct card names | %d |" % len(forge))
out("| MTGA card DB rows | %d |" % len(rows))
out("| MTGA distinct primary non-token names | %d |" % len(mtga))
out("| ... exact Forge name match | %d |" % len(exact))
out("| ... match only after folding case/accents/punctuation | %d |" % len(fold_only))
out("| ... no Forge script | %d |" % len(mtga_missing))
out("| Forge names with a GrpId (primary printing) | %d |" % len(forge_on_arena))
out("| Forge names only on non-primary rows (tokens, faces, alt printings) | %d |" % len(forge_nonprimary_only))
out("| Forge names with NO GrpId | %d |" % len(forge_missing))
out("")
out("## Forge cards without a GrpId, by category")
out("| category | names |")
out("|---|---|")
for k, v in forge_cats.most_common():
    out("| %s | %d |" % (k, v))
out("")
out("Examples (regular cards not on Arena): " + ", ".join(sorted(n for n in forge_missing if forge_category(n) == "regular card not on Arena")[:40]))
out("")
out("## MTGA primary names without a Forge script, by category")
out("| category | names |")
out("|---|---|")
for k, v in mtga_cats.most_common():
    out("| %s | %d |" % (k, v))
out("")
for k in mtga_cats:
    ex = [n for n in mtga_missing if mtga_category(n) == k][:25]
    out("- %s: %s" % (k, ", ".join(ex)))
out("")
out("## Name mismatches resolved by folding (not all real: the fold also drops '-')")
for n, f in sorted(fold_only.items())[:60]:
    out("- `%s` -> `%s`" % (n, f[0]))
out("")
out("## Split cards (`A /// B` titles in the DB)")
out("%d primary split names, e.g. %s" % (len(split_titles), ", ".join(sorted(split_titles)[:10])))
text = "\n".join(lines) + "\n"
if md_out:
    with open(os.path.join(root, md_out) if not os.path.isabs(md_out) else md_out, "w", encoding="utf-8") as fh:
        fh.write(text)
print(text)
