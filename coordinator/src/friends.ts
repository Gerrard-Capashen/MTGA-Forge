/**
 * Friends, invites and blocks (REST, Bearer token; accounts in accounts.ts). Every change tells the players it concerns through the
 * Hub (hub.ts) with {"t":"changed"}; the client then reads GET /friends again.
 *
 *   GET    /friends                          -> {friends:[{playerId,name,tag,fullName,status}], incoming:[...], outgoing:[...], blocks:[...]}
 *   POST   /friends/invite {full|playerId}   invite; when that player already invited you, you become friends
 *   POST   /friends/accept  {playerId}       accept an incoming invite
 *   POST   /friends/decline {playerId}       decline an incoming invite
 *   POST   /friends/revoke  {playerId}       take back an outgoing invite
 *   DELETE /friends/{playerId}               remove a friend
 *   POST   /blocks {full|playerId}           block (ends the friendship and invites between you)
 *   DELETE /blocks/{playerId}                unblock
 * Presence ("Available" | "Busy" | "Away" | "Offline") comes from the Hub: sockets of the /session WebSocket.
 */
import { authenticate, type AccountEnv } from "./accounts";

export interface FriendsEnv extends AccountEnv {
  HUB: DurableObjectNamespace;
}

const MAX_FRIENDS = 100;
const MAX_PENDING_OUT = 50;
const ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

interface Row { player_id: string; name: string; tag: string; created?: number }
const view = (r: Row) => ({ playerId: r.player_id, name: r.name, tag: r.tag, fullName: `${r.name}#${r.tag}` });
const fail = (status: number, error: string) => Response.json({ error }, { status });
const ok = (body: unknown = { ok: true }, status = 200) => Response.json(body, { status });
const pair = (x: string, y: string) => (x < y ? [x, y] : [y, x]);

export const hubOf = (env: FriendsEnv) => env.HUB.get(env.HUB.idFromName("hub"));

/** Tells these players' open sessions something changed (they read GET /friends again). */
async function changed(env: FriendsEnv, ...ids: string[]): Promise<void> {
  await hubOf(env).fetch("https://hub/notify", { method: "POST", body: JSON.stringify({ to: ids, event: { t: "changed" } }) });
}

async function resolve(env: FriendsEnv, body: Record<string, unknown> | null): Promise<Row | null> {
  if (!body) return null;
  if (typeof body.playerId === "string" && ID.test(body.playerId)) {
    return env.DB.prepare("SELECT player_id, name, tag FROM players WHERE player_id = ?1").bind(body.playerId).first<Row>();
  }
  const m = typeof body.full === "string" ? body.full.match(/^(.{3,24})#(\d{5})$/s) : null;
  return m ? env.DB.prepare("SELECT player_id, name, tag FROM players WHERE name_lc = ?1 AND tag = ?2").bind(m[1].normalize("NFC").toLowerCase(), m[2]).first<Row>() : null;
}

const isBlocked = async (env: FriendsEnv, blocker: string, blocked: string) =>
  !!await env.DB.prepare("SELECT 1 FROM blocks WHERE blocker = ?1 AND blocked = ?2").bind(blocker, blocked).first();

export async function handleFriends(req: Request, env: FriendsEnv, url: URL): Promise<Response | null> {
  const path = url.pathname;
  if (path !== "/friends" && !path.startsWith("/friends/") && path !== "/blocks" && !path.startsWith("/blocks/")) return null;
  const me = await authenticate(env, req);
  if (!me) return fail(401, "sign in first");
  const body = req.method === "POST" ? await req.json().catch(() => null) as Record<string, unknown> | null : null;

  if (path === "/friends" && req.method === "GET") {
    const other = "CASE WHEN f.a = ?1 THEN f.b ELSE f.a END";
    const rows = (await env.DB.prepare(
      `SELECT p.player_id, p.name, p.tag, f.created, f.status, f.requester FROM friendships f
       JOIN players p ON p.player_id = ${other} WHERE f.a = ?1 OR f.b = ?1`).bind(me).all<Row & { status: string; requester: string }>()).results;
    const friends = rows.filter(r => r.status === "accepted");
    const status: Record<string, string> = friends.length === 0 ? {} : await (await hubOf(env).fetch("https://hub/presence", {
      method: "POST", body: JSON.stringify({ ids: friends.map(f => f.player_id) }) })).json();
    const blocks = (await env.DB.prepare("SELECT p.player_id, p.name, p.tag FROM blocks b JOIN players p ON p.player_id = b.blocked WHERE b.blocker = ?1").bind(me).all<Row>()).results;
    return ok({
      friends: friends.map(r => ({ ...view(r), status: status[r.player_id] ?? "Offline" })),
      incoming: rows.filter(r => r.status === "pending" && r.requester !== me).map(r => ({ ...view(r), created: r.created })),
      outgoing: rows.filter(r => r.status === "pending" && r.requester === me).map(r => ({ ...view(r), created: r.created })),
      blocks: blocks.map(view),
    });
  }

  if (path === "/friends/invite" && req.method === "POST") {
    const them = await resolve(env, body);
    if (!them || await isBlocked(env, them.player_id, me)) return fail(404, "no such player");   // a blocked inviter learns nothing
    if (them.player_id === me) return fail(400, "you cannot add yourself");
    if (await isBlocked(env, me, them.player_id)) return fail(409, "you blocked this player: unblock first");
    const [a, b] = pair(me, them.player_id);
    const existing = await env.DB.prepare("SELECT status, requester FROM friendships WHERE a = ?1 AND b = ?2").bind(a, b).first<{ status: string; requester: string }>();
    if (existing?.status === "accepted") return fail(409, "already friends");
    if (existing && existing.requester === me) return fail(409, "already invited");
    if (existing) {                                                       // they invited you first: that is a yes
      await env.DB.prepare("UPDATE friendships SET status = 'accepted' WHERE a = ?1 AND b = ?2").bind(a, b).run();
    } else {
      const counts = await env.DB.prepare(
        `SELECT (SELECT count(*) FROM friendships WHERE (a = ?1 OR b = ?1) AND status = 'accepted') AS friends,
                (SELECT count(*) FROM friendships WHERE requester = ?1 AND status = 'pending') AS pending`).bind(me).first<{ friends: number; pending: number }>();
      if ((counts?.friends ?? 0) >= MAX_FRIENDS) return fail(409, "friend list is full");
      if ((counts?.pending ?? 0) >= MAX_PENDING_OUT) return fail(409, "too many open invites");
      await env.DB.prepare("INSERT INTO friendships (a, b, requester, status, created) VALUES (?1, ?2, ?3, 'pending', ?4)").bind(a, b, me, Date.now()).run();
    }
    await changed(env, me, them.player_id);
    return ok({ ...view(them), accepted: !!existing });
  }

  if ((path === "/friends/accept" || path === "/friends/decline" || path === "/friends/revoke") && req.method === "POST") {
    const them = await resolve(env, body);
    if (!them) return fail(404, "no such player");
    const [a, b] = pair(me, them.player_id);
    const asker = path === "/friends/revoke" ? me : them.player_id;     // accept / decline answer THEIR invite, revoke undoes MINE
    const sql = path === "/friends/accept"
      ? "UPDATE friendships SET status = 'accepted' WHERE a = ?1 AND b = ?2 AND status = 'pending' AND requester = ?3"
      : "DELETE FROM friendships WHERE a = ?1 AND b = ?2 AND status = 'pending' AND requester = ?3";
    if (path === "/friends/accept") {
      const full = await env.DB.prepare("SELECT count(*) AS n FROM friendships WHERE (a = ?1 OR b = ?1) AND status = 'accepted'").bind(me).first<{ n: number }>();
      if ((full?.n ?? 0) >= MAX_FRIENDS) return fail(409, "friend list is full");
    }
    const r = await env.DB.prepare(sql).bind(a, b, asker).run();
    if (r.meta.changes === 0) return fail(404, "no such invite");
    await changed(env, me, them.player_id);
    return ok();
  }

  if (path.startsWith("/friends/") && req.method === "DELETE") {
    const id = path.slice("/friends/".length);
    if (!ID.test(id)) return fail(400, "bad playerId");
    const [a, b] = pair(me, id);
    const r = await env.DB.prepare("DELETE FROM friendships WHERE a = ?1 AND b = ?2 AND status = 'accepted'").bind(a, b).run();
    if (r.meta.changes === 0) return fail(404, "not friends");
    await changed(env, me, id);
    return ok();
  }

  if (path === "/blocks" && req.method === "POST") {
    const them = await resolve(env, body);
    if (!them) return fail(404, "no such player");
    if (them.player_id === me) return fail(400, "you cannot block yourself");
    const [a, b] = pair(me, them.player_id);
    await env.DB.batch([
      env.DB.prepare("INSERT OR IGNORE INTO blocks (blocker, blocked, created) VALUES (?1, ?2, ?3)").bind(me, them.player_id, Date.now()),
      env.DB.prepare("DELETE FROM friendships WHERE a = ?1 AND b = ?2").bind(a, b),
    ]);
    await changed(env, me, them.player_id);
    return ok(view(them));
  }

  if (path.startsWith("/blocks/") && req.method === "DELETE") {
    const id = path.slice("/blocks/".length);
    if (!ID.test(id)) return fail(400, "bad playerId");
    const r = await env.DB.prepare("DELETE FROM blocks WHERE blocker = ?1 AND blocked = ?2").bind(me, id).run();
    if (r.meta.changes === 0) return fail(404, "not blocked");
    await changed(env, me);
    return ok();
  }
  return null;
}
