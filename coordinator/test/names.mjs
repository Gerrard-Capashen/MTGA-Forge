// Opponent names: a signed-in ticket is shown by its account's name, never by the name its URL claims.
// node test/names.mjs [http://127.0.0.1:8787]   (needs `npm run dev`; test players are "zztest...": `npm run db:clean -- --local`)
import { generateKeyPairSync, sign, randomUUID, randomBytes } from "node:crypto";

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
  const reg = await api("POST", "/register", { playerId: id, publicKey: pub, name, nonce: n, sig: sig(`register|${id}|${n}|${name}`) });
  n = (await api("POST", "/auth/challenge")).body.nonce;
  const login = await api("POST", "/auth/login", { playerId: id, nonce: n, sig: sig(`login|${id}|${n}`) });
  return { id, name, tag: reg.body.tag, token: login.body.token };
}

/** Waits in queue `q` claiming `claim` as its name; `token` signs the ticket in. Resolves the "paired" message. */
function queue(q, id, claim, token) {
  return new Promise(res => {
    const ws = new WebSocket(`${WS}/queue?q=${q}&fp=zz&v=1&id=${id}&name=${encodeURIComponent(claim)}&code=${run}`,
      token ? { headers: { authorization: "Bearer " + token } } : undefined);
    ws.onmessage = e => { const m = JSON.parse(e.data); if (m.t === "paired") res(m); };
    ws.onerror = () => res({ error: true });
    ws.onclose = () => res({ error: true });
  });
}
async function pair(q, first, second) {
  const a = queue(q, ...first);
  await sleep(200);
  const b = queue(q, ...second);
  return [await a, await b];
}

const A = await player("Alice"), B = await player("Bob");
const CASUAL = "CasualPlay_Modern";

// 1. both signed in: each sees the other's account name, even though the URLs claim someone else
let [ha, gb] = await pair(CASUAL, [A.id, "Gerrard", A.token], [B.id, "Gerrard", B.token]);
check(ha.oppVerified === true && ha.opp === B.name && ha.oppId === B.id && ha.oppTag === B.tag, "host sees the guest's account name, id and tag");
check(gb.oppVerified === true && gb.opp === A.name && gb.oppId === A.id, "guest sees the host's account name and id");

// 2. one side anonymous: its claimed name is passed on, marked unverified; the signed-in side is still vouched for
[ha, gb] = await pair(CASUAL, [A.id, "whatever", A.token], ["anon-" + run, "Gerrard", null]);
check(ha.oppVerified === false && ha.opp === "Gerrard" && ha.oppId === undefined, "an anonymous opponent is unverified, with no id");
check(gb.oppVerified === true && gb.opp === A.name, "the anonymous side is told the signed-in account's name");

// 3. a bad token signs nothing in
[ha, gb] = await pair(CASUAL, [A.id, "Mallory", "v1.bad.token.x"], [B.id, "x", B.token]);
check(gb.oppVerified === false && gb.opp === "Mallory", "a forged token is just an anonymous ticket");

// 4. ranked: the same, and the seat is the account
[ha, gb] = await pair("RankedPlay_Standard", [A.id, "Gerrard", A.token], [B.id, "Gerrard", B.token]);
check(ha.oppVerified === true && ha.opp === B.name && gb.opp === A.name, "ranked pairing shows account names");

console.log(failed ? `${failed} FAILED` : "all ok");
process.exit(failed ? 1 : 0);
