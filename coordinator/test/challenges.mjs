// Direct challenges + friend messages: node test/challenges.mjs [http://127.0.0.1:8787]
// Local: `npm run db:migrate:local`, `npm run dev`. Test players are called "zztest...": `npm run db:clean`.
import { generateKeyPairSync, sign, randomUUID, randomBytes } from "node:crypto";

const BASE = process.argv[2] ?? "http://127.0.0.1:8787";
const WS = BASE.replace(/^http/, "ws");
let failed = 0;
const check = (ok, what) => { console.log((ok ? "ok   " : "FAIL ") + what); if (!ok) failed++; };
const sleep = ms => new Promise(r => setTimeout(r, ms));
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
  const reg = await api("POST", "/register", { playerId: id, publicKey: pub, name, nonce: n, sig: sig(`register|${id}|${n}|${name}`) });
  n = (await api("POST", "/auth/challenge")).body.nonce;
  const login = await api("POST", "/auth/login", { playerId: id, nonce: n, sig: sig(`login|${id}|${n}`) });
  return { id, full: reg.body.fullName, token: login.body.token };
}

function session(p) {
  const ws = new WebSocket(`${WS}/session?token=${p.token}`);
  const msgs = [];
  ws.onmessage = e => { if (e.data !== "pong") msgs.push(JSON.parse(e.data)); };
  const opened = new Promise((res, rej) => { ws.onopen = res; ws.onerror = () => rej(new Error("session")); });
  const waitFor = async (pred, ms = 3000) => {
    const end = Date.now() + ms;
    for (;;) {
      const i = msgs.findIndex(pred);
      if (i >= 0) return msgs.splice(i, 1)[0];
      if (Date.now() > end) return undefined;
      await sleep(40);
    }
  };
  return { ws, opened, msgs, waitFor, send: o => ws.send(JSON.stringify(o)) };
}
const isC = kind => m => m.t === "challenge" && m.kind === kind;

const A = await player("alice"), B = await player("bob"), C = await player("carol");
await api("POST", "/friends/invite", { playerId: B.id }, A.token);
await api("POST", "/friends/accept", { playerId: A.id }, B.token);
const sa = session(A), sb = session(B), sc = session(C);
await Promise.all([sa.opened, sb.opened, sc.opened]);
await sleep(300);
check(true, "A and B are friends, C is a stranger, all three signed in");

// friend messages (what the client's challenge invites use)
sa.send({ t: "game", to: B.id, payload: "hello bob" });
const g = await sb.waitFor(m => m.t === "game");
check(g?.from === A.id && g.payload === "hello bob", "A's message reaches friend B (from, payload)");
sa.send({ t: "game", to: C.id, payload: "hello carol" });
await sleep(400);
check(!sc.msgs.some(m => m.t === "game"), "a message to a non-friend is dropped");
sa.send({ t: "game", to: B.id, payload: "x".repeat(20000) });
await sleep(400);
check(!sb.msgs.some(m => m.t === "game"), "an oversized message is dropped");

// create
let r = await api("POST", "/challenges", { title: "zztest game", name: "DirectGame", cosmetics: { avatar: "Avatar_Basic_Jace", emotes: ["Phrase_Basic_Hello"] } }, A.token);
const ch = r.body.challenge;
check(r.status === 200 && ch?.owner === A.id && ch.players.length === 1 && ch.name === "DirectGame" && ch.status === "open", "A creates a challenge (owner, 1 player, open)");
const id = ch.id;
r = await api("POST", "/challenges", { name: "NotAChallenge" }, A.token);
check(r.status === 400, "an invalid challenge name is refused");

// join rules
r = await api("POST", `/challenges/${id}/join`, {}, C.token);
check(r.status === 403, "a stranger cannot join");
r = await api("POST", `/challenges/${id}/join`, { cosmetics: { avatar: "Avatar_Basic_Liliana" } }, B.token);
check(r.status === 200 && r.body.challenge.players.length === 2, "friend B joins");
const st = await sa.waitFor(isC("status"));
check(st?.challenge.players.length === 2, "A gets the status push with 2 players");
r = await api("POST", `/challenges/${id}/join`, {}, C.token);
check(r.status === 403 || r.status === 409, "a third player is refused");
r = await api("GET", `/challenges/${id}`, undefined, C.token);
check(r.status === 403, "a stranger cannot read it");

// ready / settings / launch rules
r = await api("POST", `/challenges/${id}/launch`, {}, A.token);
check(r.status === 409, "launch before everybody is ready: refused");
r = await api("POST", `/challenges/${id}/settings`, { name: "DirectGameBrawl" }, B.token);
check(r.status === 403, "only the owner changes the settings");
r = await api("POST", `/challenges/${id}/ready`, { deckId: "deck-b", deckName: "Bob's deck", artId: 77, tileId: 78, cosmetics: { sleeve: "CardBack_Default" } }, B.token);
check(r.status === 200 && r.body.challenge.players.find(p => p.playerId === B.id).ready, "B is ready with a deck");
await api("POST", `/challenges/${id}/ready`, { deckId: "deck-a", deckName: "Alice's deck" }, A.token);
r = await api("POST", `/challenges/${id}/settings`, { name: "DirectGameBrawl", playFirst: "Challenger", winCondition: "BestOf3" }, A.token);
check(r.status === 200 && r.body.challenge.name === "DirectGameBrawl" && r.body.challenge.winCondition === "BestOf3" && r.body.challenge.playFirst === "Challenger" && r.body.challenge.players.every(p => !p.ready), "owner's settings applied, everybody un-readied");
r = await api("POST", `/challenges/${id}/player`, { teamId: 1 }, B.token);
check(r.status === 200 && r.body.challenge.players.find(p => p.playerId === B.id).teamId === 1, "player settings (team)");
await api("POST", `/challenges/${id}/ready`, { deckId: "deck-b", deckName: "Bob's deck" }, B.token);
await api("POST", `/challenges/${id}/unready`, {}, B.token);
r = await api("POST", `/challenges/${id}/launch`, {}, A.token);
check(r.status === 409, "launch with a player un-readied: refused");
await api("POST", `/challenges/${id}/ready`, { deckId: "deck-b", deckName: "Bob's deck" }, B.token);
await api("POST", `/challenges/${id}/ready`, { deckId: "deck-a", deckName: "Alice's deck" }, A.token);
r = await api("POST", `/challenges/${id}/launch`, {}, B.token);
check(r.status === 403, "only the owner launches");

// launch -> countdown -> issue -> match
sa.msgs.length = 0; sb.msgs.length = 0;
r = await api("POST", `/challenges/${id}/launch`, {}, A.token);
check(r.status === 200 && r.body.challenge.status === "starting", "A launches: starting");
const ca = await sa.waitFor(isC("countdown")), cb = await sb.waitFor(isC("countdown"));
check(ca?.seconds > 0 && cb?.id === id, "both get the countdown");
r = await api("POST", `/challenges/${id}/settings`, { name: "DirectGame" }, A.token);
check(r.status === 409, "settings are locked while starting");
await api("POST", `/challenges/${id}/issue`, {}, A.token);
await sleep(300);
check(!sa.msgs.some(m => m.kind === "match") && !sb.msgs.some(m => m.kind === "match"), "one issue is not enough");
await api("POST", `/challenges/${id}/issue`, {}, B.token);
const ma = await sa.waitFor(isC("match")), mb = await sb.waitFor(isC("match"));
check(ma?.role === "host" && mb?.role === "guest" && ma.match === mb.match && ma.token !== mb.token, "both get the match: A (owner) hosts, B guests, same match id");
check(ma?.opponent.playerId === B.id && mb?.opponent.playerId === A.id, "each side is told the opponent");
r = await api("GET", `/challenges/${id}`, undefined, A.token);
check(r.body.challenge.status === "open" && r.body.challenge.players.every(p => !p.ready), "back in the lobby after the match was handed over");

// the tokens open the relay room
{
  const open = (m) => { const ws = new WebSocket(`${WS}/match/${m.match}?role=${m.role}&token=${m.token}`); ws.binaryType = "arraybuffer"; return new Promise((res, rej) => { ws.onopen = () => res(ws); ws.onerror = () => rej(new Error("room")); }); };
  const [wa, wb] = await Promise.all([open(ma), open(mb)]);
  const got = new Promise(res => { wb.onmessage = e => { if (typeof e.data !== "string") res(new Uint8Array(e.data)); }; });
  wa.send(new Uint8Array([6, 1, 2, 3]));
  const frame = await Promise.race([got, sleep(3000).then(() => null)]);
  check(frame && frame.length === 4 && frame[0] === 6, "the match tokens open the relay room and frames cross");
  wa.close(); wb.close();
}

// exit / kick / close
r = await api("POST", `/challenges/${id}/exit`, {}, B.token);
check(r.status === 200 && r.body.challenge.players.length === 1, "B leaves: 1 player left");
await api("POST", `/challenges/${id}/join`, {}, B.token);
sb.msgs.length = 0;
r = await api("POST", `/challenges/${id}/kick`, { playerId: B.id }, A.token);
check(r.status === 200 && r.body.challenge.players.length === 1, "A kicks B");
check(!!await sb.waitFor(isC("kick")), "B is told about the kick");
r = await api("GET", `/challenges/${id}`, undefined, B.token);
check(r.status === 403, "B no longer sees the challenge");
await api("POST", `/challenges/${id}/join`, {}, B.token);
sb.msgs.length = 0;
r = await api("GET", "/challenges", undefined, B.token);
check(r.body.challenges.length === 1 && r.body.challenges[0].id === id, "GET /challenges lists the open challenge (reconnect)");
r = await api("POST", `/challenges/${id}/close`, {}, B.token);
check(r.status === 403, "only the owner closes");
r = await api("POST", `/challenges/${id}/close`, {}, A.token);
check(r.status === 200 && r.body.challenge === null, "A closes the challenge");
check(!!await sb.waitFor(isC("close")), "B is told it closed");
r = await api("GET", `/challenges/${id}`, undefined, A.token);
check(r.status === 404, "a closed challenge is gone");
r = await api("GET", "/challenges", undefined, B.token);
check(r.body.challenges.length === 0, "nothing open any more");

// limit of open challenges
const ids = [];
for (let i = 0; i < 5; i++) ids.push((await api("POST", "/challenges", { name: "DirectGame" }, A.token)).body.challenge?.id);
r = await api("POST", "/challenges", { name: "DirectGame" }, A.token);
check(ids.every(Boolean) && r.status === 409, "5 open challenges per player, the 6th is refused");
for (const x of ids) await api("POST", `/challenges/${x}/close`, {}, A.token);

for (const s of [sa, sb, sc]) s.ws.close();
console.log(failed ? `${failed} FAILED` : "all passed");
process.exit(failed ? 1 : 0);
