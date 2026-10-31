// Daily quests: node test/quests.mjs [http://127.0.0.1:8787]
// Local only (it moves quest days straight in the local D1): `npm run db:migrate:local`, `npm run dev`. Test players are "zztest...": `npm run db:clean -- --local`.
import { generateKeyPairSync, sign, randomUUID, randomBytes } from "node:crypto";
import { execSync } from "node:child_process";

const BASE = process.argv[2] ?? "http://127.0.0.1:8787";
let failed = 0;
const check = (ok, what) => { console.log((ok ? "ok   " : "FAIL ") + what); if (!ok) failed++; };
const run = randomBytes(3).toString("hex");

async function api(method, path, body, token) {
  const r = await fetch(BASE + path, { method, headers: { "content-type": "application/json", ...(token ? { authorization: "Bearer " + token } : {}) }, body: body === undefined ? undefined : JSON.stringify(body) });
  return { status: r.status, body: await r.json().catch(() => ({})) };
}
async function player(label) {
  const { publicKey, privateKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
  const pub = publicKey.export({ type: "spki", format: "der" }).toString("base64");
  const sig = msg => sign("sha256", Buffer.from(msg), { key: privateKey, dsaEncoding: "ieee-p1363" }).toString("base64");
  const id = randomUUID(), name = `zztest ${label} ${run}`;
  let n = (await api("POST", "/auth/challenge")).body.nonce;
  await api("POST", "/register", { playerId: id, publicKey: pub, name, nonce: n, sig: sig(`register|${id}|${n}|${name}`) });
  n = (await api("POST", "/auth/challenge")).body.nonce;
  const login = await api("POST", "/auth/login", { playerId: id, nonce: n, sig: sig(`login|${id}|${n}`) });
  return { id, token: login.body.token };
}
const sql = cmd => JSON.parse(execSync(`npx wrangler d1 execute mtga-forge --local --json --command "${cmd.replace(/\s*\n\s*/g, " ")}"`, { encoding: "utf8" }))[0].results;
const quests = async p => (await api("GET", "/me/quests", undefined, p.token)).body.quests;
const progress = (p, match, metrics) => api("POST", "/me/quests/progress", { match, metrics }, p.token);
const swap = (p, questId) => api("POST", "/me/quests/swap", { questId }, p.token);
const mastery = async p => (await api("GET", "/me/mastery", undefined, p.token)).body;
// the counter a quest kind adds up (as in quests.ts)
const METRIC = { PlayVintage: "MatchPlayed_Vintage", PlayLegacy: "MatchPlayed_Legacy", PlayPauper: "MatchPlayed_Pauper", PlayPremodern: "MatchPlayed_Premodern", PlayModern: "MatchPlayed_Modern",
  PlayPioneer: "MatchPlayed_Pioneer", WinStandard: "MatchWon_Standard", PlayRanked: "RankedPlayed", PlayDraft: "DraftPlayed", AttackCreatures: "CreaturesAttacked",
  CastWhiteSpells: "WhiteSpellsCast", CastBlueSpells: "BlueSpellsCast", CastBlackSpells: "BlackSpellsCast", CastRedSpells: "RedSpellsCast", CastGreenSpells: "GreenSpellsCast",
  CastInstantsSorceries: "InstantsAndSorceriesCast", CastCreatures: "CreaturesCast", PlayLands: "LandsPlayed", DealDamage: "DamageDealt",
  KillCreatures: "OpponentCreaturesDied", CastArtifactsEnchantments: "ArtifactsAndEnchantmentsCast" };

const A = await player("alice");
check((await api("GET", "/me/quests")).status === 401, "quests need a sign-in");
let q = await quests(A);
check(q.length === 3 && new Set(q.map(x => x.kind)).size === 3 && q.every(x => x.progress === 0 && x.canSwap && !x.done && METRIC[x.kind]),
  `a new player gets three different quests (${q.map(x => x.kind).join(", ")})`);
check(JSON.stringify(await quests(A)) === JSON.stringify(q), "asking again the same day gives the same quests");

// progress
const [q0, q1, q2] = q;
let r = await progress(A, "m1", { [METRIC[q0.kind]]: 1, SomethingElse: 50 });
check(r.status === 200 && r.body.updates.length === 1 && r.body.updates[0].questId === q0.questId && r.body.updates[0].from === 0 && r.body.updates[0].to === 1,
  "a match's counters move only the quest that counts them");
r = await progress(A, "m1", { [METRIC[q0.kind]]: 5 });
check(r.body.updates.length === 0 && (await quests(A)).find(x => x.questId === q0.questId).progress === 1, "the same match counts once");

// completion pays XP once
const before = (await mastery(A)).xp;
r = await progress(A, "m2", { [METRIC[q1.kind]]: 1000 });
const u = r.body.updates.find(x => x.questId === q1.questId);
check(u && u.to === q1.goal && u.xp === q1.xp, `reaching the goal finishes the quest (${q1.kind} ${q1.goal}) and says its XP (${q1.xp})`);
check((await mastery(A)).xp === before + q1.xp, "the quest's XP is paid into mastery");
r = await progress(A, "m3", { [METRIC[q1.kind]]: 1000 });
check(r.body.updates.length === 0 && (await mastery(A)).xp === before + q1.xp, "a finished quest takes no more progress and pays once");
q = await quests(A);
check(q.find(x => x.questId === q1.questId).done && !q.find(x => x.questId === q1.questId).canSwap, "a finished quest stays shown as done today and cannot be swapped");

// swap: once per quest per day
r = await swap(A, q2.questId);
const after = r.body.quests;
const replaced = after.find(x => x.questId !== q0.questId && x.questId !== q1.questId);
check(r.status === 200 && replaced && replaced.questId !== q2.questId && !after.some(x => x.questId === q2.questId) && replaced.progress === 0 && !replaced.canSwap,
  `a swap gives a new quest (${q2.kind} -> ${replaced?.kind}) that cannot be swapped again today`);
check(new Set(after.map(x => x.kind)).size === 3, "the new quest is not one of the other two");
check((await swap(A, replaced.questId)).status === 409, "a swapped slot cannot be swapped again the same day");
check((await swap(A, q1.questId)).status === 409, "a finished quest cannot be swapped");
check((await quests(A)).find(x => x.questId === q0.questId).canSwap, "the other quests keep their own swap");

// the next day: the finished quest is replaced, the unfinished ones stay with their progress; swaps come back
sql(`UPDATE quests SET done_day = done_day - 1, swap_day = swap_day - 1, assigned_day = assigned_day - 1 WHERE player_id = '${A.id}'`);
q = await quests(A);
check(q.length === 3 && !q.some(x => x.questId === q1.questId) && q.some(x => x.questId === q0.questId && x.progress === 1) && q.some(x => x.questId === replaced.questId),
  "next day: the finished quest is replaced, unfinished ones keep their progress");
check(q.every(x => x.canSwap), "next day: every quest can be swapped again");

console.log(failed ? `${failed} FAILED` : "all ok");
process.exit(failed ? 1 : 0);
