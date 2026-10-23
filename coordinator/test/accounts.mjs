// Accounts: node test/accounts.mjs [http://127.0.0.1:8787] [concurrent registrations, default 200]
// Local: `npm run db:migrate:local` once, `npm run dev`. Test players are called "zztest...": remove with `npm run db:clean` (add --remote for production).
import { generateKeyPairSync, sign, randomUUID, randomBytes } from "node:crypto";

const BASE = process.argv[2] ?? "http://127.0.0.1:8787";
const N = Number(process.argv[3] ?? 200);
let failed = 0;
const check = (ok, what) => { console.log((ok ? "ok   " : "FAIL ") + what); if (!ok) failed++; };

const run = randomBytes(3).toString("hex");                      // letters+digits: names of this run
const post = (path, body, token, method = "POST") => fetch(BASE + path, {
  method, headers: { "content-type": "application/json", ...(token ? { authorization: "Bearer " + token } : {}) }, body: body === undefined ? undefined : JSON.stringify(body),
}).then(async r => ({ status: r.status, body: await r.json().catch(() => ({})) }));
const get = (path, token) => fetch(BASE + path, { headers: token ? { authorization: "Bearer " + token } : {} }).then(async r => ({ status: r.status, body: await r.json().catch(() => ({})) }));

function newKey() {
  const { publicKey, privateKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
  return { priv: privateKey, pub: publicKey.export({ type: "spki", format: "der" }).toString("base64") };
}
const sig = (k, msg) => sign("sha256", Buffer.from(msg), { key: k.priv, dsaEncoding: "ieee-p1363" }).toString("base64");
const nonce = async () => (await post("/auth/challenge")).body.nonce;

async function register(k, id, name, tag) {
  const n = await nonce();
  return post("/register", { playerId: id, publicKey: k.pub, name, tag, nonce: n, sig: sig(k, `register|${id}|${n}|${name}`) });
}
async function login(k, id) {
  const n = await nonce();
  return post("/auth/login", { playerId: id, nonce: n, sig: sig(k, `login|${id}|${n}`) });
}

const alice = { k: newKey(), id: randomUUID(), name: "zztest alice " + run };
let r = await register(alice.k, alice.id, alice.name);
check(r.status === 201 && /^\d{5}$/.test(r.body.tag) && r.body.fullName === `${alice.name}#${r.body.tag}`, `register: ${r.body.fullName}`);
alice.tag = r.body.tag;

r = await register(alice.k, alice.id, alice.name);
check(r.status === 200 && r.body.tag === alice.tag, "registering again with the same key is idempotent (same tag)");
r = await register(newKey(), alice.id, alice.name);
check(r.status === 409, "another key cannot take over the player id");
{
  const k = newKey(), id = randomUUID(), n = await nonce();
  r = await post("/register", { playerId: id, publicKey: k.pub, name: "zztest bad sig " + run, nonce: n, sig: sig(newKey(), `register|${id}|${n}|zztest bad sig ${run}`) });
  check(r.status === 401, "signature by another key rejected");
  r = await post("/register", { playerId: id, publicKey: k.pub, name: "zztest old " + run, nonce: "1.x.y", sig: sig(k, "x") });
  check(r.status === 401, "bad nonce rejected");
  for (const bad of ["ab", "x".repeat(25), "has#hash", "  ", "<script>"]) {
    r = await register(k, randomUUID(), bad);
    if (r.status !== 400) { check(false, `name "${bad}" rejected (got ${r.status})`); }
  }
  check(true, "bad names rejected");
}

// login, /me, lookup
r = await login(newKey(), alice.id);
check(r.status === 401, "login with another key rejected");
r = await login(alice.k, alice.id);
check(r.status === 200 && r.body.token, "login");
alice.token = r.body.token;
r = await get("/me", alice.token);
check(r.status === 200 && r.body.fullName === `${alice.name}#${alice.tag}`, "GET /me");
r = await get("/me");
check(r.status === 401, "/me needs a token");
r = await get("/me", alice.token.slice(0, -2) + "00");
check(r.status === 401, "forged token rejected");
r = await get("/players/by-name?full=" + encodeURIComponent(`${alice.name.toUpperCase()}#${alice.tag}`), alice.token);
check(r.status === 200 && r.body.playerId === alice.id, "lookup by full name, case-insensitive");
check(r.body.dev === false, "a new player is not a developer (the flag is only set by hand in the database)");
r = await get("/players/by-id?id=" + alice.id, alice.token);
check(r.status === 200 && r.body.fullName === `${alice.name}#${alice.tag}` && r.body.dev === false, "lookup by player id carries the dev flag");
r = await get("/players/by-id?id=" + alice.id);
check(r.status === 401, "lookup by player id needs a token");
r = await get("/players/by-id?id=" + randomUUID(), alice.token);
check(r.status === 404, "unknown player id: 404");

// the same name: every player gets a different tag, also all at once
const same = "zztest same " + run;
const batch = await Promise.all(Array.from({ length: N }, () => register(newKey(), randomUUID(), same)));
const ok = batch.filter(x => x.status === 201);
const tags = new Set(ok.map(x => x.body.tag));
check(ok.length === N && tags.size === N, `${N} simultaneous registrations of one name: ${ok.length} created, ${tags.size} distinct tags` + (ok.length < N ? ` (statuses ${[...new Set(batch.map(x => x.status))]})` : ""));

// keeping a known tag (migration of a local profile), but only once
const wanted = "12345", mig = "zztest keep " + run;
const a1 = await register(newKey(), randomUUID(), mig, wanted), a2 = await register(newKey(), randomUUID(), mig, wanted);
check(a1.status === 201 && a1.body.tag === wanted, "a free wanted tag is kept");
check(a2.status === 201 && a2.body.tag !== wanted, "a taken wanted tag gives a different one");

// rename: new tag, the old pair stays reserved
const bob = { k: newKey(), id: randomUUID(), name: "zztest bob " + run };
const rb = await register(bob.k, bob.id, bob.name);
bob.token = (await login(bob.k, bob.id)).body.token;
r = await post("/me", { name: "zztest robert " + run }, bob.token, "PATCH");
check(r.status === 200 && r.body.name === "zztest robert " + run && /^\d{5}$/.test(r.body.tag), `rename: ${r.body.fullName}`);
r = await get("/players/by-name?full=" + encodeURIComponent(`${bob.name}#${rb.body.tag}`), alice.token);
check(r.status === 404, "the old name#tag no longer resolves");
const sniper = await register(newKey(), randomUUID(), bob.name, rb.body.tag);
check(sniper.status === 201 && sniper.body.tag !== rb.body.tag, "the old pair is reserved for a while");
r = await post("/me", { name: bob.name.toUpperCase() }, bob.token, "PATCH");
check(r.status === 200, "rename to a bad-case variant works");
r = await post("/me", { name: "no" }, bob.token, "PATCH");
check(r.status === 400, "rename to an invalid name rejected");

console.log(failed ? `${failed} FAILED` : "all passed");
process.exit(failed ? 1 : 0);
