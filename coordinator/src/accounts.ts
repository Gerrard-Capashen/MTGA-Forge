/**
 * Accounts without passwords: a device key (ECDSA P-256) proves who acts as a player id; the player's public name is "Name#12345"
 * where many players may share the name and the pair is unique (UNIQUE(name_lc, tag) in D1 decides, not a check before the insert).
 *
 *   POST  /auth/challenge                                  -> {nonce}                       (stateless, 2 minutes)
 *   POST  /register {playerId, publicKey, name, tag?, nonce, sig}   sig over "register|playerId|nonce|name"
 *   POST  /auth/login {playerId, nonce, sig}               sig over "login|playerId|nonce"  -> {token, exp, ...}
 *   GET   /me                                              (Bearer token)
 *   PATCH /me {name}                                       rename: new tag, the old pair stays reserved for 30 days
 *   GET   /players/by-name?full=Name%2312345               (Bearer token) -> {playerId, name, tag}
 *
 * publicKey = SPKI DER, base64; sig = IEEE P1363 (r||s), base64 (Java: SHA256withECDSAinP1363Format).
 */

export interface AccountEnv {
  DB: D1Database;
  TOKEN_SECRET?: string;
}

const NONCE_MS = 120_000;
const TOKEN_MS = 24 * 3_600_000;
const HOLD_MS = 30 * 24 * 3_600_000;
const NAME = /^[\p{L}\p{N}_\- ]{3,24}$/u;
const ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const TAG_TRIES = 20;

const enc = new TextEncoder();
const unb64 = (s: string) => Uint8Array.from(atob(s), c => c.charCodeAt(0));
const hex = (bytes: ArrayBuffer) => [...new Uint8Array(bytes)].map(b => b.toString(16).padStart(2, "0")).join("");

async function mac(env: AccountEnv, text: string): Promise<string> {
  const key = await crypto.subtle.importKey("raw", enc.encode(env.TOKEN_SECRET ?? "dev-secret"), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  return hex(await crypto.subtle.sign("HMAC", key, enc.encode(text)));
}

function json(status: number, body: unknown): Response {
  return Response.json(body, { status });
}
const fail = (status: number, error: string) => json(status, { error });

/** Name rules: 3-24 letters/digits/_/-/space, no edge or double spaces, no '#'. Returns the cleaned name or null. */
export function cleanName(raw: unknown): string | null {
  if (typeof raw !== "string") return null;
  const n = raw.normalize("NFC").trim().replace(/ {2,}/g, " ");
  return NAME.test(n) ? n : null;
}

async function makeNonce(env: AccountEnv): Promise<string> {
  const body = `${Date.now()}.${crypto.randomUUID()}`;
  return `${body}.${await mac(env, "nonce|" + body)}`;
}

async function checkNonce(env: AccountEnv, nonce: unknown): Promise<boolean> {
  if (typeof nonce !== "string") return false;
  const parts = nonce.split(".");
  if (parts.length !== 3) return false;
  const at = Number(parts[0]);
  if (!Number.isFinite(at) || Date.now() - at > NONCE_MS || at > Date.now() + 5_000) return false;
  return parts[2] === await mac(env, `nonce|${parts[0]}.${parts[1]}`);
}

async function verify(publicKey: string, message: string, sig: string): Promise<boolean> {
  try {
    const key = await crypto.subtle.importKey("spki", unb64(publicKey), { name: "ECDSA", namedCurve: "P-256" }, false, ["verify"]);
    return await crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, key, unb64(sig), enc.encode(message));
  } catch {
    return false;
  }
}

async function issueToken(env: AccountEnv, playerId: string): Promise<{ token: string; exp: number }> {
  const exp = Date.now() + TOKEN_MS;
  const body = `${playerId}.${exp}`;
  return { token: `v1.${body}.${await mac(env, "session|" + body)}`, exp };
}

/** The player id behind a valid "Authorization: Bearer" token, else null. */
export async function authenticate(env: AccountEnv, req: Request): Promise<string | null> {
  const h = req.headers.get("Authorization") ?? "";
  const t = h.startsWith("Bearer ") ? h.slice(7).split(".") : [];
  if (t.length !== 4 || t[0] !== "v1" || !ID.test(t[1]) || Number(t[2]) < Date.now()) return null;
  return t[3] === await mac(env, `session|${t[1]}.${t[2]}`) ? t[1] : null;
}

interface PlayerRow { player_id: string; name: string; name_lc: string; tag: string; pubkey: string }

const view = (p: PlayerRow) => ({ playerId: p.player_id, name: p.name, tag: p.tag, fullName: `${p.name}#${p.tag}` });

function randomTag(): string {
  const n = new Uint32Array(1);
  crypto.getRandomValues(n);
  return String(n[0] % 100_000).padStart(5, "0");
}

const isUnique = (e: unknown) => String((e as Error)?.message ?? e).includes("UNIQUE");

/**
 * Gives the player "name#tag": the wanted tag first (a returning player keeps theirs), then random ones. A taken pair is the
 * database's UNIQUE constraint, a reserved pair (recent rename) is the NOT EXISTS; either just means "try another tag".
 * Returns the tag or null when every try failed.
 */
async function allocate(env: AccountEnv, write: (name: string, nameLc: string, tag: string, now: number) => D1PreparedStatement,
                        name: string, wanted: string | null): Promise<string | null> {
  const nameLc = name.toLowerCase();
  for (let i = 0; i < TAG_TRIES; i++) {
    const tag = i === 0 && wanted ? wanted : randomTag();
    try {
      const r = await write(name, nameLc, tag, Date.now()).run();
      if (r.meta.changes > 0) return tag;
    } catch (e) {
      if (!isUnique(e)) throw e;
    }
  }
  return null;
}

const NOT_HELD = "NOT EXISTS (SELECT 1 FROM name_holds WHERE name_lc = ?1 AND tag = ?2 AND until > ?3)";

export async function handleAccounts(req: Request, env: AccountEnv, url: URL): Promise<Response | null> {
  const path = url.pathname;
  if (path === "/auth/challenge" && req.method === "POST") return json(200, { nonce: await makeNonce(env) });

  if (path === "/register" && req.method === "POST") {
    const b = await req.json().catch(() => null) as Record<string, unknown> | null;
    if (!b) return fail(400, "bad json");
    const name = cleanName(b.name);
    if (typeof b.playerId !== "string" || !ID.test(b.playerId)) return fail(400, "bad playerId");
    if (typeof b.publicKey !== "string" || b.publicKey.length > 300) return fail(400, "bad publicKey");
    if (!name) return fail(400, "bad name: 3-24 letters, digits, space, _ or -");
    if (!await checkNonce(env, b.nonce)) return fail(401, "bad or expired nonce");
    if (typeof b.sig !== "string" || !await verify(b.publicKey, `register|${b.playerId}|${b.nonce}|${name}`, b.sig)) return fail(401, "bad signature");

    const existing = await env.DB.prepare("SELECT * FROM players WHERE player_id = ?1").bind(b.playerId).first<PlayerRow>();
    if (existing) return existing.pubkey === b.publicKey ? json(200, view(existing)) : fail(409, "playerId belongs to another key");

    const wanted = typeof b.tag === "string" && /^\d{5}$/.test(b.tag) ? b.tag : null;
    const tag = await allocate(env, (n, lc, t, now) => env.DB.prepare(
      `INSERT INTO players (player_id, name, name_lc, tag, pubkey, created, last_seen)
       SELECT ?4, ?5, ?1, ?2, ?6, ?3, ?3 WHERE ${NOT_HELD}`).bind(lc, t, now, b.playerId, n, b.publicKey), name, wanted);
    if (!tag) return fail(503, "no free tag, try again");
    return json(201, view({ player_id: b.playerId as string, name, name_lc: name.toLowerCase(), tag, pubkey: b.publicKey as string }));
  }

  if (path === "/auth/login" && req.method === "POST") {
    const b = await req.json().catch(() => null) as Record<string, unknown> | null;
    if (!b || typeof b.playerId !== "string" || !ID.test(b.playerId)) return fail(400, "bad playerId");
    if (!await checkNonce(env, b.nonce)) return fail(401, "bad or expired nonce");
    const p = await env.DB.prepare("SELECT * FROM players WHERE player_id = ?1").bind(b.playerId).first<PlayerRow & { status: string }>();
    if (!p) return fail(404, "not registered");
    if (p.status !== "ok") return fail(403, "account suspended");
    if (typeof b.sig !== "string" || !await verify(p.pubkey, `login|${b.playerId}|${b.nonce}`, b.sig)) return fail(401, "bad signature");
    await env.DB.prepare("UPDATE players SET last_seen = ?2 WHERE player_id = ?1").bind(p.player_id, Date.now()).run();
    return json(200, { ...await issueToken(env, p.player_id), ...view(p) });
  }

  if (path === "/me" || path === "/players/by-name") {
    const id = await authenticate(env, req);
    if (!id) return fail(401, "sign in first");

    if (path === "/players/by-name" && req.method === "GET") {
      const m = (url.searchParams.get("full") ?? "").match(/^(.{3,24})#(\d{5})$/s);
      if (!m) return fail(400, "expected Name#12345");
      const p = await env.DB.prepare("SELECT * FROM players WHERE name_lc = ?1 AND tag = ?2").bind(m[1].normalize("NFC").toLowerCase(), m[2]).first<PlayerRow>();
      return p ? json(200, view(p)) : fail(404, "no such player");
    }

    const me = await env.DB.prepare("SELECT * FROM players WHERE player_id = ?1").bind(id).first<PlayerRow>();
    if (!me) return fail(404, "not registered");
    if (req.method === "GET") return json(200, view(me));

    if (req.method === "PATCH") {
      const b = await req.json().catch(() => null) as Record<string, unknown> | null;
      const name = cleanName(b?.name);
      if (!name) return fail(400, "bad name: 3-24 letters, digits, space, _ or -");
      if (name === me.name) return json(200, view(me));
      if (name.toLowerCase() === me.name_lc) {                               // only the capitalisation changes: the pair stays
        await env.DB.prepare("UPDATE players SET name = ?2 WHERE player_id = ?1").bind(id, name).run();
        return json(200, view({ ...me, name }));
      }
      const tag = await allocate(env, (n, lc, t, now) => env.DB.prepare(
        `UPDATE players SET name = ?4, name_lc = ?1, tag = ?2 WHERE player_id = ?5 AND ${NOT_HELD}`).bind(lc, t, now, n, id), name, null);
      if (!tag) return fail(503, "no free tag, try again");
      await env.DB.prepare("INSERT OR REPLACE INTO name_holds (name_lc, tag, until) VALUES (?1, ?2, ?3)").bind(me.name_lc, me.tag, Date.now() + HOLD_MS).run();
      return json(200, view({ ...me, name, name_lc: name.toLowerCase(), tag }));
    }
  }
  return null;
}
