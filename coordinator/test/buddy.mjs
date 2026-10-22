// A second player for trying the real client by hand: node test/buddy.mjs <Name#12345 to invite> [seconds alive] [coordinator url]
// Registers a throw-away player "zztest buddy ...", signs in (session, so it shows Online), invites the given player after 5 s,
// prints what happens (changed pushes, friend list, presence). Once you are friends it cycles Busy, Away, Available, Offline (8 s apart) so the presence can be watched.
import { generateKeyPairSync, sign, randomUUID, randomBytes } from "node:crypto";

const target = process.argv[2];
const alive = Number(process.argv[3] ?? 120);
const BASE = process.argv[4] ?? "https://mm.mtga-forge.workers.dev";
const WS = BASE.replace(/^http/, "ws");
if (!target) { console.log("usage: node test/buddy.mjs 'Name#12345' [seconds] [url]"); process.exit(2); }

const api = async (method, path, body, token) => {
  const r = await fetch(BASE + path, { method, headers: { "content-type": "application/json", ...(token ? { authorization: "Bearer " + token } : {}) }, body: body === undefined ? undefined : JSON.stringify(body) });
  return { status: r.status, body: await r.json().catch(() => ({})) };
};
const { publicKey, privateKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
const pub = publicKey.export({ type: "spki", format: "der" }).toString("base64");
const sig = m => sign("sha256", Buffer.from(m), { key: privateKey, dsaEncoding: "ieee-p1363" }).toString("base64");
const id = randomUUID(), name = "zztest buddy " + randomBytes(2).toString("hex");
let n = (await api("POST", "/auth/challenge")).body.nonce;
const reg = await api("POST", "/register", { playerId: id, publicKey: pub, name, nonce: n, sig: sig(`register|${id}|${n}|${name}`) });
n = (await api("POST", "/auth/challenge")).body.nonce;
const token = (await api("POST", "/auth/login", { playerId: id, nonce: n, sig: sig(`login|${id}|${n}`) })).body.token;
console.log("buddy is", reg.body.fullName);

const ws = new WebSocket(`${WS}/session?token=${token}`);
ws.onmessage = async e => {
  if (e.data === "pong") return;
  const m = JSON.parse(e.data);
  console.log("push", e.data);
  if (m.t === "changed") {
    const f = (await api("GET", "/friends", undefined, token)).body;
    console.log("  friends:", f.friends.map(x => `${x.fullName} (${x.status})`), "incoming:", f.incoming.map(x => x.fullName), "outgoing:", f.outgoing.map(x => x.fullName));
    if (f.friends.length && !globalThis.cycled) {
      globalThis.cycled = true;
      for (const [ms, st] of [[8000, "Busy"], [16000, "Away"], [24000, "Available"], [32000, "Offline"]]) setTimeout(() => { console.log("buddy:", st); ws.send(JSON.stringify({ t: "status", status: st })); }, ms);
    }
    if (f.incoming.length) for (const i of f.incoming) { console.log("  accepting", i.fullName); await api("POST", "/friends/accept", { playerId: i.playerId }, token); }
  }
};
await new Promise(r => { ws.onopen = r; });
setInterval(() => ws.send("ping"), 20000);

setTimeout(async () => { const r = await api("POST", "/friends/invite", { full: target }, token); console.log("invite", target, "->", r.status, JSON.stringify(r.body)); }, 5000);
setTimeout(() => { console.log("buddy leaves"); ws.close(); setTimeout(() => process.exit(0), 500); }, alive * 1000);
