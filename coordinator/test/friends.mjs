// Friends, presence, blocks: node test/friends.mjs [http://127.0.0.1:8787]
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
  return { id, name, full: reg.body.fullName, token: login.body.token };
}

/** A session WebSocket that records every message. */
function session(p, status) {
  const ws = new WebSocket(`${WS}/session?token=${p.token}${status ? "&status=" + status : ""}`);
  const msgs = [];
  ws.onmessage = e => { if (e.data !== "pong") msgs.push(JSON.parse(e.data)); };
  const closed = new Promise(r => ws.addEventListener("close", e => r(e.code)));
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
  return { ws, opened, closed, msgs, waitFor };
}

const A = await player("alice"), B = await player("bob"), C = await player("carol");
check(A.token && B.token && C.token, "three players registered and signed in");

// no session without a valid token
{
  const bad = new WebSocket(`${WS}/session?token=nope`);
  check(await new Promise(r => { bad.onopen = () => r(false); bad.onerror = () => r(true); bad.onclose = () => r(true); }), "session without a valid token refused");
}

// invites
const sb = session(B); await sb.opened;
check((await sb.waitFor(m => m.t === "hello"))?.status === "Available", "session says hello");
let r = await api("POST", "/friends/invite", { full: B.full }, A.token);
check(r.status === 200 && r.body.accepted === false, "A invites B by full name");
check(!!await sb.waitFor(m => m.t === "changed"), "B's session is told something changed");
r = await api("GET", "/friends", undefined, B.token);
check(r.body.incoming.length === 1 && r.body.incoming[0].fullName === A.full && r.body.friends.length === 0, "B sees the incoming invite");
r = await api("GET", "/friends", undefined, A.token);
check(r.body.outgoing.length === 1 && r.body.outgoing[0].fullName === B.full, "A sees the outgoing invite");
r = await api("POST", "/friends/invite", { full: B.full }, A.token);
check(r.status === 409, "inviting twice refused");
r = await api("POST", "/friends/invite", { full: A.full }, A.token);
check(r.status === 400, "inviting yourself refused");
r = await api("POST", "/friends/invite", { full: "Nobody Here#00000" }, A.token);
check(r.status === 404, "inviting an unknown player: 404");
r = await api("POST", "/friends/accept", { playerId: A.id }, A.token);
check(r.status === 404, "A cannot accept their own invite");

// presence: A online, B accepts
const sa = session(A); await sa.opened; await sa.waitFor(m => m.t === "hello");
await sa.waitFor(m => m.t === "presence", 300);
r = await api("POST", "/friends/accept", { playerId: A.id }, B.token);
check(r.status === 200, "B accepts");
check(!!await sa.waitFor(m => m.t === "changed"), "A is told about the new friend");
r = await api("GET", "/friends", undefined, A.token);
check(r.body.friends.length === 1 && r.body.friends[0].fullName === B.full && r.body.friends[0].status === "Available" && r.body.outgoing.length === 0, "A's list: B, Available");

// presence changes reach friends
await sb.ws.send(JSON.stringify({ t: "status", status: "Busy" }));
let p = await sa.waitFor(m => m.t === "presence" && m.playerId === B.id);
check(p?.status === "Busy", "B goes Busy: A is told");
await sb.ws.send(JSON.stringify({ t: "status", status: "Offline" }));
p = await sa.waitFor(m => m.t === "presence" && m.playerId === B.id);
check(p?.status === "Offline", "B appears Offline (socket stays): A is told");
await sb.ws.send(JSON.stringify({ t: "status", status: "Available" }));
await sa.waitFor(m => m.t === "presence" && m.playerId === B.id);
sb.ws.close();
p = await sa.waitFor(m => m.t === "presence" && m.playerId === B.id);
check(p?.status === "Offline", "B disconnects: A is told Offline");
r = await api("GET", "/friends", undefined, A.token);
check(r.body.friends[0].status === "Offline", "A's list shows B Offline");
const sb2 = session(B); await sb2.opened;
p = await sa.waitFor(m => m.t === "presence" && m.playerId === B.id);
check(p?.status === "Available", "B signs in again: A is told Available");
const sb3 = session(B); await sb3.opened;
check(await sb2.closed === 4000, "a second session replaces the first");
await sleep(300);

// a stranger gets no presence of people who are not friends
const sc = session(C); await sc.opened; await sc.waitFor(m => m.t === "hello");
await sleep(300);
check(!sc.msgs.some(m => m.t === "presence"), "a non-friend is told nothing about presence");

// decline / revoke
r = await api("POST", "/friends/invite", { playerId: C.id }, A.token);
check(r.status === 200, "A invites C");
r = await api("POST", "/friends/decline", { playerId: A.id }, C.token);
check(r.status === 200, "C declines");
r = await api("GET", "/friends", undefined, A.token);
check(r.body.outgoing.length === 0 && r.body.friends.length === 1, "the declined invite is gone");
await api("POST", "/friends/invite", { playerId: C.id }, A.token);
r = await api("POST", "/friends/revoke", { playerId: C.id }, A.token);
check(r.status === 200, "A revokes an invite");
r = await api("GET", "/friends", undefined, C.token);
check(r.body.incoming.length === 0, "C no longer sees it");

// both invite each other: friends at once
await api("POST", "/friends/invite", { playerId: C.id }, A.token);
r = await api("POST", "/friends/invite", { playerId: A.id }, C.token);
check(r.status === 200 && r.body.accepted === true, "mutual invites: friends");
r = await api("DELETE", "/friends/" + C.id, undefined, A.token);
check(r.status === 200, "A removes C");
r = await api("GET", "/friends", undefined, C.token);
check(r.body.friends.length === 0, "C's list is empty again");

// blocks
r = await api("POST", "/blocks", { playerId: B.id }, A.token);
check(r.status === 200, "A blocks B");
r = await api("GET", "/friends", undefined, A.token);
check(r.body.friends.length === 0 && r.body.blocks.length === 1 && r.body.blocks[0].fullName === B.full, "blocking ends the friendship and lists B in the blocks");
r = await api("POST", "/friends/invite", { full: A.full }, B.token);
check(r.status === 404, "the blocked player cannot invite A (gets 'no such player')");
r = await api("POST", "/friends/invite", { full: B.full }, A.token);
check(r.status === 409, "A cannot invite B while B is blocked");
r = await api("DELETE", "/blocks/" + B.id, undefined, A.token);
check(r.status === 200, "A unblocks B");
r = await api("POST", "/friends/invite", { full: A.full }, B.token);
check(r.status === 200, "after unblocking B can invite again");

for (const s of [sa, sb3, sc]) s.ws.close();
console.log(failed ? `${failed} FAILED` : "all passed");
process.exit(failed ? 1 : 0);
