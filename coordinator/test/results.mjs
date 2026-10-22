// Match results: node test/results.mjs [http://127.0.0.1:8787]
// Local: `npm run db:migrate:local`, `npm run dev`. Test players are called "zztest...": `npm run db:clean`.
import { generateKeyPairSync, sign, randomUUID, randomBytes } from "node:crypto";

const BASE = process.argv[2] ?? "http://127.0.0.1:8787";
const WS = BASE.replace(/^http/, "ws");
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

/** Two players through the real queue: returns each one's match id, role and token. */
async function paired(a, b, q) {
  const wait = (p) => new Promise((res, rej) => {
    const ws = new WebSocket(`${WS}/queue?q=${q}&fp=zz&v=1&id=${p.id}&name=${p.id.slice(0, 8)}&code=${run}`);
    ws.onmessage = e => { const m = JSON.parse(e.data); if (m.t === "paired") res(m); };
    ws.onerror = () => rej(new Error("queue"));
  });
  const first = wait(a);
  await new Promise(r => setTimeout(r, 200));
  const second = wait(b);
  return [await first, await second];
}

const A = await player("alice"), B = await player("bob"), C = await player("carol");
const report = (p, m, outcome, pool = "CasualPlay_Modern", extra = {}) =>
  api("POST", `/matches/${m.match}/result`, { role: m.role, token: m.token, outcome, pool, ...extra }, p.token);

let [a, b] = await paired(A, B, "CasualPlay_Modern");
check(a.role === "host" && b.role === "guest", "A and B are paired (A hosts)");

let r = await api("POST", `/matches/${a.match}/result`, { role: a.role, token: a.token, outcome: "win", pool: "CasualPlay_Modern" });
check(r.status === 401, "a report without a sign-in is refused");
r = await report(A, a, "win", "CasualPlay_Modern", { token: "0".repeat(64) });
check(r.status === 403, "a wrong match token is refused");
r = await report(A, a, "win", "bad pool!");
check(r.status === 400, "a malformed pool is refused");
r = await report(A, a, "won");
check(r.status === 400, "an unknown outcome is refused");

r = await report(A, a, "win");
check(r.status === 200 && r.body.status === "pending", "host reports win: pending until the guest reports");
r = await report(A, a, "win");
check(r.status === 200 && r.body.status === "pending", "repeating the same report is fine");
r = await report(A, a, "loss");
check(r.status === 409, "a seat cannot change its report");
r = await api("GET", `/matches/${a.match}`, undefined, C.token);
check(r.status === 404, "a stranger cannot read the match");
r = await report(B, b, "loss");
check(r.status === 200 && r.body.status === "verified" && r.body.winner === "host", "guest reports loss: verified, host won");
r = await api("GET", `/matches/${a.match}`, undefined, B.token);
check(r.status === 200 && r.body.status === "verified" && r.body.winner === "host", "GET shows the verdict to a player");

// disagreement: both claim the win
[a, b] = await paired(A, B, "CasualPlay_Modern");
await report(A, a, "win"); r = await report(B, b, "win");
check(r.body.status === "disputed" && r.body.winner === undefined, "both claiming a win is disputed, with no winner");

// draw
[a, b] = await paired(A, B, "CasualPlay_Modern");
await report(B, b, "draw"); r = await report(A, a, "draw");
check(r.body.status === "verified" && r.body.winner === "draw", "draw/draw is verified as a draw (guest reported first)");

// pool mismatch
[a, b] = await paired(A, B, "CasualPlay_Modern");
await report(A, a, "win", "CasualPlay_Modern"); r = await report(B, b, "loss", "CasualPlay_Standard");
check(r.body.status === "disputed", "reports from different pools are disputed");

// one player in both seats
[a, b] = await paired(A, B, "CasualPlay_Modern");
await report(A, a, "win"); r = await report(A, b, "loss");
check(r.status === 409, "one account cannot fill both seats of a match");

// a verdict is final
[a, b] = await paired(A, B, "CasualPlay_Modern");
await report(A, a, "win"); await report(B, b, "loss");
r = await report(B, b, "win");
check(r.status === 409, "a verified match cannot be re-reported");

// -- appended by the disconnect work: forfeit / conceded / void paths (dev server runs with RESULT_MIN_MS=300, RESULT_GRACE_MS=600)
import { execSync } from "node:child_process";
const sleep = ms => new Promise(r => setTimeout(r, ms));
// local D1 straight from the database: lets a test see what the coordinator settled WITHOUT anyone asking
const dbStatus = id => JSON.parse(execSync(`npx wrangler d1 execute mtga-forge --local --json --command "SELECT status FROM matches WHERE match_id = '${id}'"`, { encoding: "utf8" }))[0].results[0]?.status ?? null;
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
const status = async (p, m) => (await api("GET", `/matches/${m.match}`, undefined, p.token)).body;

// the guest drops after the minimum match length, the host reports a win: pending through the grace, then a forfeit
[a, b] = await paired(A, B, "CasualPlay_Modern");
let [ra, rb] = await bothIn(a, b);
await sleep(450);
rb.ws.close();
await sleep(150);
r = await report(A, a, "win");
check(r.body.status === "pending", "host win right after the guest dropped: still pending (grace period)");
await sleep(1800);
check(dbStatus(a.match) === "forfeit", "the room settles the forfeit by itself after the grace period (nobody asked)");
r = await status(A, a);
check(r.status === "forfeit" && r.winner === "host", "after the grace period the host's win is a forfeit win");
r = await report(B, b, "loss");
check(r.body.status === "verified" && r.body.winner === "host", "the dropped guest later reporting the loss upgrades it to verified");

// ... or contradicting it
[a, b] = await paired(A, B, "CasualPlay_Modern");
[ra, rb] = await bothIn(a, b);
await sleep(450); rb.ws.close(); await sleep(150);
await report(A, a, "win"); await sleep(800); await status(A, a);
r = await report(B, b, "win");
check(r.body.status === "disputed", "the dropped guest claiming the win too turns the forfeit into a dispute");

// the host drops: the guest's win counts the same way
[a, b] = await paired(A, B, "CasualPlay_Modern");
[ra, rb] = await bothIn(a, b);
await sleep(450); ra.ws.close(); await sleep(150);
await report(B, b, "win"); await sleep(800);
r = await status(B, b);
check(r.status === "forfeit" && r.winner === "guest", "host drops: the guest's win is a forfeit win");

// dropping at once does not give the other seat anything
[a, b] = await paired(A, B, "CasualPlay_Modern");
[ra, rb] = await bothIn(a, b);
rb.ws.close(); await sleep(150);
await report(A, a, "win"); await sleep(800);
r = await status(A, a);
check(r.status === "pending", "a drop before the minimum match length is not a forfeit");

// a win with nobody having dropped is not accepted on its own
[a, b] = await paired(A, B, "CasualPlay_Modern");
[ra, rb] = await bothIn(a, b);
await sleep(450);
await report(A, a, "win"); await sleep(800);
r = await status(A, a);
check(r.status === "pending", "a lone win with both seats still connected stays pending");
ra.ws.close(); rb.ws.close();

// a lone loss is taken at its word, and the other seat can still report
[a, b] = await paired(A, B, "CasualPlay_Modern");
r = await report(A, a, "loss");
check(r.body.status === "conceded", "a lone loss is conceded");
r = await report(B, b, "win");
check(r.body.status === "verified" && r.body.winner === "guest", "the other seat reporting the win makes it verified");

console.log(failed ? `${failed} FAILED` : "all ok");
process.exit(failed ? 1 : 0);
