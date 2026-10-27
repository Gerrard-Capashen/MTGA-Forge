// Refreshes the Bot Play deck pool from MTGGoldfish's metagame pages: the archetype page embeds the list of its representative
// deck (the /deck pages and downloads are behind a bot check, which this does not touch). One request per 2 s.
//   node tools/fetch-goldfish-decks.mjs [format ...]      -> forge-gui-mtga/src/main/resources/bot-decks/<Format>/<Name>.txt + index.txt
import fs from "node:fs";
import path from "node:path";

const OUT = path.resolve(import.meta.dirname, "../forge/forge-gui-mtga/src/main/resources/bot-decks");
const FORMATS = ["standard", "pioneer", "modern", "legacy", "vintage", "pauper"];
// combo / storm / reanimator decks: playing against them blind is no fun, and the AI pilots them badly
const COMBO = /combo|storm|reanimator|doomsday|aluren|sneak|goryo|greasefang|self-bounce|indomitable|amulet|oath|dredge|breach|paradoxical|excruciator|spy|familiars/;
const UA = { "User-Agent": "Mozilla/5.0 (MTGA-Forge bot deck pool refresh)" };
const sleep = ms => new Promise(r => setTimeout(r, ms));

async function get(url) {
  const r = await fetch(url, { headers: UA });
  if (!r.ok) throw new Error(`${url}: ${r.status}`);
  return r.text();
}

const decode = s => s.replace(/&#39;/g, "'").replace(/&amp;/g, "&").replace(/&quot;/g, '"');
const want = process.argv.slice(2).length ? process.argv.slice(2) : FORMATS;
fs.mkdirSync(OUT, { recursive: true });
const indexFile = path.join(OUT, "index.txt");
const index = new Set(fs.existsSync(indexFile) ? fs.readFileSync(indexFile, "utf8").split(/\r?\n/).filter(Boolean) : []);

for (const format of want) {
  const meta = await get(`https://www.mtggoldfish.com/metagame/${format}`);
  const slugs = [...new Set([...meta.matchAll(/href="\/archetype\/([^"#]+)/g)].map(m => m[1]))].slice(0, 15);
  const Format = format[0].toUpperCase() + format.slice(1);
  fs.mkdirSync(path.join(OUT, Format), { recursive: true });
  for (const slug of slugs) {
    await sleep(2000);
    if (COMBO.test(slug)) { console.log(`${Format}: skip ${slug} (combo)`); continue; }
    const html = await get(`https://www.mtggoldfish.com/archetype/${slug}`);
    const list = html.match(/deck_input_deck" value="([^"]*)"/);
    const title = html.match(/<title>(.*?) Deck for /);
    if (!list || !title) { console.log(`${Format}: no list for ${slug}`); continue; }
    const name = decode(title[1]).replace(/[\\/:*?"<>|]/g, "").trim();
    const text = decode(list[1]).replace(/\r/g, "").replace(/^sideboard$/m, "").trim() + "\n";   // the list marks its sideboard with a word, ours with a blank line
    fs.writeFileSync(path.join(OUT, Format, name + ".txt"), text);
    index.add(`${Format}/${name}.txt`);
    console.log(`${Format}: ${name} (${text.split("\n").length - 1} lines)`);
  }
}
fs.writeFileSync(indexFile, [...index].sort().join("\n") + "\n");
