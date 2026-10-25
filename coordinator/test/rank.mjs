// Rank: node test/rank.mjs [http://127.0.0.1:8787]
// Local only (it sets ratings rows straight in the local D1): `npm run db:migrate:local`, `npm run dev`
// (RESULT_MIN_MS=300 makes 300 ms the minimum ranked match length). Test players are "zztest...": `npm run db:clean -- --local`.
import { generateKeyPairSync, sign, randomUUID, randomBytes } from "node:crypto";
import { execSync } from "node:child_process";

const BASE = process.argv[2] ?? "http://127.0.0.1:8787";
const WS = BASE.replace(/^http/, "ws");
let failed = 0;
const check = (ok, what) => { console.log((ok ? "ok   " : "FAIL ") + what); if (!ok) failed++; };
const run = randomBytes(3).toString("hex");
const sleep = ms => new Promise(r => setTimeout(r, ms));
const Q = "RankedPlay_Standard";

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

/** Waits in a queue; `auth` = send the session token as a header (ranked). Resolves the pairing, or {error} when the socket fails/closes first. */
function queue(p, q, auth = true, id = p.id, keep = []) {
  return new Promise(res => {
    const ws = new WebSocket(`${WS}/queue?q=${q}&fp=zz&v=1&id=${id}&name=${p.id.slice(0, 8)}&code=${run}`, auth ? { headers: { authorization: "Bearer " + p.token } } : undefined);
    keep.push(ws);
    ws.onmessage = e => { const m = JSON.parse(e.data); if (m.t === "paired") res(m); };
    ws.onerror = () => res({ error: true });
    ws.onclose = () => res({ error: true });
  });
}
async function paired(a, b, q = Q) {
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
const report = (p, m, outcome, pool = Q) => api("POST", `/matches/${m.match}/result`, { role: m.role, token: m.token, outcome, pool }, p.token);
const verdict = async (p, m) => (await api("GET", `/matches/${m.match}`, undefined, p.token)).body;
const rank = async p => (await api("GET", "/me/rank", undefined, p.token)).body.pools?.constructed;
const sql = cmd => JSON.parse(execSync(`npx wrangler d1 execute mtga-forge --local --json --command "${cmd.replace(/\s*\n\s*/g, " ")}"`, { encoding: "utf8" }))[0].results;
const CLASS = { Bronze: 0, Silver: 1, Gold: 2, Platinum: 3, Diamond: 4, Mythic: 5 };
const setRank = (p, cls, level, step, rating = 1500) => sql(`INSERT OR REPLACE INTO ratings (player_id, pool, season, class, level, step, rating, mythic_at, version, updated)
  VALUES ('${p.id}', 'constructed', 1, ${CLASS[cls]}, ${level}, ${step}, ${rating}, ${cls === "Mythic" ? Date.now() : "NULL"}, 100, 0)`);
const pips = r => r ? `${r.class} ${r.level} ${r.step}` : "none";

/** A full ranked match: both in the room for `ms`, then the reports. Returns [hostMatch, guestMatch]. */
async function play(host, guest, hostOutcome, ms = 450) {
  const [a, b] = await paired(host, guest);
  const [ra, rb] = await bothIn(a, b);
  await sleep(ms);
  const opposite = { win: "loss", loss: "win", draw: "draw" };
  await report(host, a, hostOutcome);
  await report(guest, b, opposite[hostOutcome]);
  ra.ws.close(); rb.ws.close();
  return [a, b];
}

const A = await player("alice"), B = await player("bob");

// ---- tickets
let x = await queue(A, Q, false);
check(x.error === true, "a ranked ticket without a session token is refused");
let [a, b] = await paired(A, B);
check(a.role === "host" && b.role === "guest", "two signed-in players pair in a ranked queue");
x = sql(`SELECT role, player_id, pool FROM match_seats WHERE match_id = '${a.match}' ORDER BY role`);
check(x.length === 2 && x[0].player_id === B.id && x[1].player_id === A.id && x[0].pool === "constructed", "the pairing records both seats' accounts (pool constructed)");
let r = await report(B, a, "win");
check(r.status === 403, "another account cannot report a ranked seat even with its match token");
{
  const open = [];
  void queue(A, Q, true, "spoofed-" + B.id, open);
  await sleep(200);
  const second = await Promise.race([queue(A, Q, true, A.id, open), sleep(1500).then(() => null)]);
  check(second === null, "the same account never pairs with itself in ranked (the query id is ignored)");
  for (const w of open) w.close();
  await sleep(200);
}

// ---- a first win / loss
check(pips(await rank(A)) === "Bronze 4 0", "a new player starts at Bronze 4, step 0");
[a, b] = await play(A, B, "win");
let va = await verdict(A, a), vb = await verdict(B, b);
check(va.status === "verified" && va.rank?.reason === "applied", "verified ranked win is applied");
check(pips(va.rank?.old) === "Bronze 4 0" && pips(va.rank?.new) === "Bronze 4 2", "Bronze win: +2 pips (Bronze 4 0 -> 2)");
check(pips(vb.rank?.new) === "Bronze 4 0" && vb.rank?.lossProtected === true, "Bronze loss costs nothing (loss protected)");
check(va.rank?.newRating === 1516 && vb.rank?.newRating === 1484, "Elo: equal ratings, the winner gains 16 and the loser drops 16");
await verdict(A, a); await verdict(B, b); await report(A, a, "win");
let ra = await rank(A);
check(ra.wins === 1 && ra.losses === 0 && ra.step === 2, "idempotent: asking again / re-reporting applies nothing twice");
check(sql(`SELECT COUNT(*) AS n FROM rating_events WHERE match_id = '${a.match}'`)[0].n === 2, "exactly one rating event per player");

// ---- level and class changes (rows set straight in D1)
const C = await player("carol"), D = await player("dave");
setRank(C, "Bronze", 1, 2); setRank(D, "Silver", 4, 0);
[a, b] = await play(C, D, "win");
va = await verdict(C, a); vb = await verdict(D, b);
check(pips(va.rank?.new) === "Silver 4 0", "Bronze 1 step 2 + win -> Silver 4 step 0 (class change)");
check(pips(vb.rank?.new) === "Silver 4 0" && vb.rank?.lossProtected === true, "Silver 4 step 0 + loss: protected at the class floor");
setRank(C, "Silver", 4, 5); setRank(D, "Silver", 3, 0);
[a, b] = await play(C, D, "win");
va = await verdict(C, a); vb = await verdict(D, b);
check(pips(va.rank?.new) === "Silver 3 0", "Silver 4 step 5 + win -> Silver 3 step 0 (level up)");
check(pips(vb.rank?.new) === "Silver 4 5" && vb.rank?.lossProtected === false, "Silver 3 step 0 + loss -> Silver 4 step 5 (level down within the class)");
setRank(C, "Gold", 2, 3); setRank(D, "Gold", 2, 3);
[a, b] = await play(C, D, "draw");
va = await verdict(C, a);
check(pips(va.rank?.new) === "Gold 2 3" && va.rank?.result === "draw", "a draw moves no pips");

// ---- Mythic (new players: C and D have had their 3 results of the day against each other)
const M = await player("mia"), N = await player("noah");
setRank(M, "Diamond", 1, 5, 1600); setRank(N, "Mythic", 1, 0, 1700);
[a, b] = await play(M, N, "win");
va = await verdict(M, a); vb = await verdict(N, b);
check(pips(va.rank?.new) === "Mythic 1 0", "Diamond 1 step 5 + win -> Mythic");
check(va.rank?.newRating > 1600 + 16, "beating a higher-rated player gains more than 16");
check(pips(vb.rank?.new) === "Mythic 1 0", "a Mythic player who loses stays Mythic");
const rc = await rank(M), rd = await rank(N);
check(rc.place >= 1 && rd.place >= 1 && (rc.rating > rd.rating ? rc.place < rd.place : rc.place > rd.place), "Mythic places follow the rating");
check(rc.percentile > 0 && rc.percentile <= 100, "Mythic players get a percentile");
check((await rank(A)).place === 0, "below Mythic there is no place");

// ---- what moves nothing
const E = await player("erin"), F = await player("frank");
setRank(E, "Gold", 3, 2); setRank(F, "Gold", 3, 2);
[a, b] = await paired(E, F);
await report(E, a, "win"); await report(F, b, "loss");                       // never in the room: shorter than the minimum
va = await verdict(E, a);
check(va.status === "verified" && va.rank?.reason === "short" && pips(va.rank?.new) === "Gold 3 2", "a match below the minimum length is recorded but moves nothing");
[a, b] = await paired(E, F);
let [rA, rB] = await bothIn(a, b); await sleep(450);
await report(E, a, "win"); await report(F, b, "win"); rA.ws.close(); rB.ws.close();
va = await verdict(E, a);
check(va.status === "disputed" && va.rank === undefined, "a disputed match moves nothing");
[a, b] = await paired(E, F);
[rA, rB] = await bothIn(a, b); await sleep(450);
await report(E, a, "win"); rA.ws.close(); rB.ws.close();
va = await verdict(E, a);
check(va.status === "pending" && va.rank === undefined, "a pending match moves nothing");

// a casual match reported with a ranked pool name counts for nothing
[a, b] = await paired(E, F, "CasualPlay_Standard");
[rA, rB] = await bothIn(a, b); await sleep(450);
await report(E, a, "win", Q); await report(F, b, "loss", Q); rA.ws.close(); rB.ws.close();
va = await verdict(E, a);
check(va.status === "verified" && va.rank === undefined && pips(await rank(E)) === "Gold 3 2", "a casual match reported as ranked moves nothing");

// ---- forfeit: the leaver takes the loss
setRank(E, "Gold", 3, 2); setRank(F, "Gold", 3, 2);
[a, b] = await paired(E, F);
[rA, rB] = await bothIn(a, b);
await sleep(450); rB.ws.close(); await sleep(150);
await report(E, a, "win");
await sleep(1800);                                                           // grace 600 ms: the room settles it by itself
const ev = sql(`SELECT player_id, result, new_step FROM rating_events WHERE match_id = '${a.match}' ORDER BY result`);
check(ev.length === 2 && ev[0].player_id === F.id && ev[0].result === "loss" && ev[0].new_step === 1, "forfeit: the leaver (who never reported) takes the loss, applied by the room's alarm");
va = await verdict(E, a);
check(va.status === "forfeit" && pips(va.rank?.new) === "Gold 3 3", "forfeit: the winner gets the pip");
r = await report(F, b, "win");
check(r.body.status === "disputed" && pips(await rank(F)) === "Gold 3 1", "a later contradicting report makes it disputed but does not undo applied events");

// ---- conceded: the loser's own loss only, the winner when they report
[a, b] = await paired(E, F);
[rA, rB] = await bothIn(a, b); await sleep(450);
r = await report(F, b, "loss");
check(r.body.status === "conceded" && pips(await rank(F)) === "Gold 3 0" && pips(await rank(E)) === "Gold 3 3", "conceded: only the loser's pip moves");
await report(E, a, "win"); rA.ws.close(); rB.ws.close();
check(pips(await rank(E)) === "Gold 3 4" && pips(await rank(F)) === "Gold 3 0", "the winner's report later makes it verified: the winner's pip now, the loser's not twice");

// ---- per-pair daily cap (3 applied results per ordered pair per day)
const G = await player("gina"), H = await player("hugo");
for (let i = 0; i < 3; i++) await play(G, H, "win");
check((await rank(G)).wins === 3, "three wins against the same opponent count");
[a, b] = await play(G, H, "win");
va = await verdict(G, a); vb = await verdict(H, b);
check(va.rank?.reason === "pair-cap" && (await rank(G)).wins === 3, "the fourth result against the same opponent today is capped");
check(vb.rank?.reason === "pair-cap", "the cap holds for the other player of the pair too");

console.log(failed ? `${failed} FAILED` : "all ok");
process.exit(failed ? 1 : 0);
