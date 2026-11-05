// Mastery: node test/mastery.mjs [http://127.0.0.1:8787]
// Local only (it sets progress rows straight in the local D1): `npm run db:migrate:local`, `npm run dev`
// (RESULT_MIN_MS=300 / RESULT_GRACE_MS=600 for the forfeit path, MASTERY_BOT_GAP_MS=1500). Test players are "zztest...": `npm run db:clean -- --local`.
import { generateKeyPairSync, sign, randomUUID, randomBytes } from "node:crypto";
import { execSync } from "node:child_process";

const BASE = process.argv[2] ?? "http://127.0.0.1:8787";
const WS = BASE.replace(/^http/, "ws");
let failed = 0;
const check = (ok, what) => { console.log((ok ? "ok   " : "FAIL ") + what); if (!ok) failed++; };
const run = randomBytes(3).toString("hex");
const sleep = ms => new Promise(r => setTimeout(r, ms));

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

function queue(p, q) {
  return new Promise(res => {
    const ws = new WebSocket(`${WS}/queue?q=${q}&fp=zz&v=1&id=${p.id}&name=${p.id.slice(0, 8)}&code=${run}`, { headers: { authorization: "Bearer " + p.token } });
    ws.onmessage = e => { const m = JSON.parse(e.data); if (m.t === "paired") res(m); };
    ws.onerror = () => res({ error: true });
  });
}
async function paired(a, b, q = "CasualPlay_Modern") {
  const first = queue(a, q);
  await sleep(200);
  const second = queue(b, q);
  return [await first, await second];
}
async function room(m) {
  const ws = new WebSocket(`${WS}/match/${m.match}?role=${m.role}&token=${m.token}`);
  const state = { ws, peer: false };
  ws.onmessage = e => { if (typeof e.data === "string" && JSON.parse(e.data).t === "peer") state.peer = true; };
  await new Promise((res, rej) => { ws.onopen = res; ws.onerror = () => rej(new Error("room")); });
  return state;
}
async function bothIn(a, b) {
  const ra = await room(a), rb = await room(b);
  for (let i = 0; i < 50 && !(ra.peer && rb.peer); i++) await sleep(50);
  return [ra, rb];
}
const report = (p, m, outcome, pool = "CasualPlay_Modern") => api("POST", `/matches/${m.match}/result`, { role: m.role, token: m.token, outcome, pool }, p.token);
const verdict = async (p, m) => (await api("GET", `/matches/${m.match}`, undefined, p.token)).body;
const mastery = async p => (await api("GET", "/me/mastery", undefined, p.token)).body;
const bot = (p, match) => api("POST", "/me/xp/bot", { match }, p.token);
const sql = cmd => JSON.parse(execSync(`npx wrangler d1 execute mtga-forge --local --json --command "${cmd.replace(/\s*\n\s*/g, " ")}"`, { encoding: "utf8" }))[0].results;
const today = Math.floor(Date.now() / 86_400_000);
const setProgress = (p, xp, dayXp = 0) => sql(`INSERT OR REPLACE INTO progress (player_id, xp, level, day, day_xp, version, updated)
  VALUES ('${p.id}', ${xp}, ${Math.floor(xp / 1000)}, ${today}, ${dayXp}, 100, 0)`);
async function play(a, b, hostOutcome, q) {
  const [x, y] = await paired(a, b, q);
  const [rx, ry] = await bothIn(x, y);
  await sleep(450);
  await report(a, x, hostOutcome, q);
  await report(b, y, { win: "loss", loss: "win", draw: "draw" }[hostOutcome], q);
  rx.ws.close(); ry.ws.close();
  return [x, y];
}

// ---- the track
let r = await api("GET", "/mastery/track");
const track = r.body;
check(r.status === 200 && Array.isArray(track.items) && track.items.length === track.version && track.items.length > 2000, `the track is public (${track.items?.length} levels)`);
check(!track.items.some(([, id]) => ["Avatar_Basic_Adventurer", "CardBack_Default", "Phrase_Basic_Hello"].includes(id)), "the always-owned cosmetics are not on the track");
check(track.items[track.items.length - 1][1] === "Title_Core_Champion", "the champion title is the last level");
check(new Set(track.items.map(([, id]) => id)).size === track.items.length, "no cosmetic twice");

const A = await player("alice"), B = await player("bob");
let m = await mastery(A);
check(m.xp === 0 && m.level === 0 && m.levelXp === 1000 && m.owned.avatars.length === 0, "a new player: 0 XP, level 0, nothing granted");
check((await api("GET", "/me/mastery")).status === 401, "/me/mastery needs a sign-in");

// ---- bot matches
r = await bot(A, "forge-1");
check(r.status === 200 && r.body.gained === 100 && r.body.xp === 100, "a bot match pays 100 XP");
r = await bot(A, "forge-1");
check(r.body.gained === 100 && r.body.xp === 100, "the same bot match id pays once");
check((await bot(A, "bad id!")).status === 400, "a malformed bot match id is refused");

// ---- event prizes (Archenemy: Sparky): 10,000 XP per stage, once each
const ev = (p, event) => api("POST", "/me/xp/event", { event }, p.token);
const P = await player("prize");
r = await ev(P, "archenemy_1");
check(r.status === 200 && r.body.gained === 10000 && r.body.xp === 10000 && r.body.level === 10, "the first Sparky stage pays 10,000 XP");
r = await ev(P, "archenemy_1");
check(r.body.gained === 10000 && r.body.xp === 10000, "the same stage pays once");
r = await ev(P, "archenemy_2");
check(r.body.xp === 20000, "the second stage pays its own 10,000 XP");
check((await ev(P, "archenemy_9")).status === 400 && (await api("POST", "/me/xp/event", { event: "archenemy_3" })).status === 401, "an unknown event is refused, and a prize needs a sign-in");

// ---- online matches (casual)
let [a, b] = await play(A, B, "win", "CasualPlay_Modern");
let va = await verdict(A, a), vb = await verdict(B, b);
check(va.status === "verified" && va.mastery?.xp === 400, "verified win: 250 for playing + 150 for the win");
check(vb.mastery?.xp === 250, "verified loss: 250 for playing");
await verdict(A, a); await report(A, a, "win");
check((await mastery(A)).xp === 500 && (await mastery(B)).xp === 250, "asking again pays nothing twice");
[a, b] = await play(A, B, "draw", "CasualPlay_Modern");
check((await verdict(A, a)).mastery?.xp === 250 && (await verdict(B, b)).mastery?.xp === 250, "a draw pays 250 each");

// disputed: nothing
[a, b] = await paired(A, B);
await report(A, a, "win"); await report(B, b, "win");
va = await verdict(A, a);
check(va.status === "disputed" && va.mastery === undefined, "a disputed match pays nothing");

// conceded: the loser's 250
[a, b] = await paired(A, B);
r = await report(B, b, "loss");
check(r.body.status === "conceded" && (await verdict(B, b)).mastery?.xp === 250, "conceded: the player who reported the loss gets 250");
await report(A, a, "win");
check((await verdict(A, a)).mastery?.xp === 400 && (await verdict(B, b)).mastery?.xp === 250, "the winner's later report pays the winner, not the loser twice");

// forfeit: the winner, never the leaver
[a, b] = await paired(A, B);
let [ra, rb] = await bothIn(a, b);
await sleep(450); rb.ws.close(); await sleep(150);
await report(A, a, "win");
await sleep(1800);
va = await verdict(A, a);
check(va.status === "forfeit" && va.mastery?.xp === 400 && sql(`SELECT COUNT(*) AS n FROM xp_events WHERE award = '${a.match}'`)[0].n === 1, "forfeit: the winner gets 400, the leaver nothing");

// ranked pays too
const C = await player("carol"), D = await player("dave");
[a, b] = await play(C, D, "loss", "RankedPlay_Standard");
check((await verdict(C, a)).mastery?.xp === 250 && (await verdict(D, b)).mastery?.xp === 400, "a ranked match pays XP like any online match");

// ---- levels and grants
const E = await player("erin");
setProgress(E, 950);
r = await bot(E, "lvl-1");
m = await mastery(E);
check(m.level === 1 && m.xp === 1050, "950 + 100 XP -> level 1");
const LIST = { avatar: "avatars", sleeve: "sleeves", pet: "pets", emote: "emotes", title: "titles" };
check([0, 1].every(i => m.owned[LIST[track.items[i][0]]].includes(track.items[i][1])) && Object.values(m.owned).flat().length === 2,
  `level 1 grants the track's first two cosmetics (${track.items[0][1]}, ${track.items[1][1]})`);
await sleep(1600);                                                                    // the bot-match gap
sql(`UPDATE progress SET xp = 2950 WHERE player_id = '${E.id}'`);                       // still level 1: the next award crosses levels 2 and 3
await bot(E, "lvl-2");
const g = sql(`SELECT cosmetic_id, source FROM cosmetic_grants WHERE player_id = '${E.id}' ORDER BY source`);
check(g.length === 6 && [2, 3].every(i => g.some(x => x.cosmetic_id === track.items[i][1] && x.source === "mastery:2"))
  && [4, 5].every(i => g.some(x => x.cosmetic_id === track.items[i][1] && x.source === "mastery:3")),
  "crossing levels 2 and 3 at once grants both levels' two cosmetics (levels never skipped)");

// ---- no daily cap; bot XP at most once per MASTERY_BOT_GAP_MS (1500 ms in the dev server)
const F = await player("frank");
setProgress(F, 0, 9000);
r = await bot(F, "gap-1");
check(r.status === 200 && r.body.gained === 100, "no daily cap: 9,000 XP earned today and a match still pays");
r = await bot(F, "gap-2");
check(r.status === 429 && r.body.gained === 0, "a second bot match right after the first pays nothing (429)");
r = await bot(F, "gap-1");
check(r.status === 200 && r.body.gained === 100, "repeating an already paid bot match is fine (not the gap)");
await sleep(1600);
r = await bot(F, "gap-3");
check(r.status === 200 && r.body.gained === 100 && r.body.xp === 200, "after the gap a bot match pays again");

console.log(failed ? `${failed} FAILED` : "all ok");
process.exit(failed ? 1 : 0);
