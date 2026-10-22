// Another player for trying direct challenges in the real client:
//   node test/challenge-buddy.mjs <Name#12345 of the real client's player> [seconds alive] [coordinator url]
// Registers a throw-away player "zztest cbuddy ...", befriends the target (the client accepts in its Friends list), then creates a challenge and
// invites the target the way the real client does (an opaque friend message with the client's own JSON format). From there it plays the other seat
// through the coordinator: when the client joins it readies up, when both are ready it launches, and after the countdown it issues. Prints every push.
import { generateKeyPairSync, sign, randomUUID, randomBytes } from "node:crypto";

const target = process.argv[2];
const alive = Number(process.argv[3] ?? 200);
const BASE = process.argv[4] ?? "https://mm.mtga-forge.workers.dev";
const WS = BASE.replace(/^http/, "ws");
if (!target) { console.log("usage: node test/challenge-buddy.mjs 'Name#12345' [seconds] [url]"); process.exit(2); }
const PREFIX = "\u009b\u0080\u0099\u0091\u0092";
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);

const api = async (method, path, body, token) => {
  const r = await fetch(BASE + path, { method, headers: { "content-type": "application/json", ...(token ? { authorization: "Bearer " + token } : {}) }, body: body === undefined ? undefined : JSON.stringify(body) });
  return { status: r.status, body: await r.json().catch(() => ({})) };
};
const { publicKey, privateKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
const pub = publicKey.export({ type: "spki", format: "der" }).toString("base64");
const sig = m => sign("sha256", Buffer.from(m), { key: privateKey, dsaEncoding: "ieee-p1363" }).toString("base64");
const id = randomUUID(), name = "zztest cbuddy " + randomBytes(2).toString("hex");
let n = (await api("POST", "/auth/challenge")).body.nonce;
const reg = await api("POST", "/register", { playerId: id, publicKey: pub, name, nonce: n, sig: sig(`register|${id}|${n}|${name}`) });
const me = reg.body.fullName;
n = (await api("POST", "/auth/challenge")).body.nonce;
const token = (await api("POST", "/auth/login", { playerId: id, nonce: n, sig: sig(`login|${id}|${n}`) })).body.token;
log("buddy is", me);
const t = (await api("GET", "/players/by-name?full=" + encodeURIComponent(target), undefined, token)).body;
if (!t.playerId) { log("no such player", target); process.exit(1); }

const ws = new WebSocket(`${WS}/session?token=${token}`);
let challengeId = null, invited = false, readied = false, launched = false;

const player = (pid, full) => ({ PlayerId: pid, FullDisplayName: full, PlayerStatus: 0, Cosmetics: {}, DeckArtId: 0, DeckTileId: 0, DeckId: "00000000-0000-0000-0000-000000000000", TeamId: 0 });

async function createAndInvite() {
  const r = await api("POST", "/challenges", { title: `${name}'s challenge`, name: "DirectGame", cosmetics: { avatar: "", sleeve: "CardBack_Default", pet: "", title: "", emotes: [] } }, token);
  challengeId = r.body.challenge.id;
  log("challenge", challengeId);
  const invite = { Version: "1.0.0", IncomingInvite: {
    ChallengeId: challengeId, ChallengeTitle: r.body.challenge.title, Sender: player(id, me), Recipient: player(t.playerId, t.fullName),
    InviteStatus: 1, InviteSentTime: new Date().toISOString(), ChallengeType: "DirectGame", IsBestofThree: false } };
  ws.send(JSON.stringify({ t: "game", to: t.playerId, payload: PREFIX + JSON.stringify(invite) }));
  log("invite sent to", t.fullName);
}

ws.onmessage = async e => {
  if (e.data === "pong") return;
  const m = JSON.parse(e.data);
  if (m.t === "changed" && !invited) {
    const f = (await api("GET", "/friends", undefined, token)).body;
    log("friends:", f.friends.map(x => `${x.fullName} (${x.status})`));
    if (f.friends.some(x => x.playerId === t.playerId)) { invited = true; setTimeout(createAndInvite, 3000); }
    return;
  }
  if (m.t === "game") { log("game message from", m.from, m.payload.slice(0, 160).replace(/[^\x20-\x7e]/g, "?")); return; }
  if (m.t !== "challenge") { log("push", e.data); return; }
  if (m.kind === "status") {
    const c = m.challenge;
    log("status:", c.status, c.name, c.winCondition, c.players.map(p => `${p.fullName}${p.ready ? " READY" : ""}${p.deckName ? " [" + p.deckName + "]" : ""}`).join(" | "));
    if (c.players.length === 2 && !readied) {
      readied = true;
      setTimeout(async () => { log("buddy ready ->", (await api("POST", `/challenges/${challengeId}/ready`, { deckId: "buddy-deck", deckName: "Buddy deck", cosmetics: { avatar: "", sleeve: "CardBack_Default" } }, token)).status); }, 2000);
    }
    if (c.status === "open" && c.players.length === 2 && c.players.every(p => p.ready) && !launched) {
      launched = true;
      setTimeout(async () => { const r = await api("POST", `/challenges/${challengeId}/launch`, {}, token); log("launch ->", r.status, JSON.stringify(r.body).slice(0, 120)); }, 2500);
    }
  } else if (m.kind === "countdown") {
    log("countdown", m.seconds, "s");
    setTimeout(async () => { log("buddy issues ->", (await api("POST", `/challenges/${challengeId}/issue`, {}, token)).status); }, (m.seconds + 3) * 1000);
  } else if (m.kind === "match") {
    log("MATCH", JSON.stringify({ role: m.role, match: m.match, opponent: m.opponent.fullName }));
  } else log("challenge push", e.data.slice(0, 200));
};
await new Promise(r => { ws.onopen = r; });
setInterval(() => ws.send("ping"), 20000);
setTimeout(async () => { log("invite", target); const r = await api("POST", "/friends/invite", { full: target }, token); log("invite ->", r.status); }, 3000);
setTimeout(async () => { if (challengeId) await api("POST", `/challenges/${challengeId}/close`, {}, token); log("buddy leaves"); ws.close(); setTimeout(() => process.exit(0), 500); }, alive * 1000);
