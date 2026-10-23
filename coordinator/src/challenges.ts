/**
 * Direct challenges (REST, Bearer token). The state lives in the Challenge Durable Object (challenge.ts); this file authenticates,
 * checks friendships and forwards.
 *
 *   POST /challenges {title, name, cosmetics}        create (you are the owner); name = "DirectGame", "DirectGameBrawl", ...
 *   GET  /challenges                                 the open challenges you are in (a reconnect)
 *   GET  /challenges/{id}
 *   POST /challenges/{id}/join {cosmetics}           needs a friendship with the owner
 *   POST /challenges/{id}/settings {name, playFirst, winCondition}   owner
 *   POST /challenges/{id}/player {teamId}  /ready {deckId, deckName, artId, tileId, cosmetics}  /unready
 *   POST /challenges/{id}/launch (owner)   /issue (each player, after the countdown)   /exit   /close (owner)   /kick {playerId} (owner)
 * Answers {challenge} (null once closed) or {error}. Pushes to the players' sessions: see challenge.ts.
 */
import { authenticate } from "./accounts";
import type { ChallengeEnv } from "./challenge";

export interface ChallengesEnv extends ChallengeEnv {
  CHALLENGE: DurableObjectNamespace;
}

const ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const OPS = ["join", "settings", "player", "ready", "unready", "launch", "issue", "exit", "close", "kick"];
const MAX_OPEN = 5;

const fail = (status: number, error: string) => Response.json({ error }, { status });
const stub = (env: ChallengesEnv, id: string) => env.CHALLENGE.get(env.CHALLENGE.idFromName(id));
const call = (env: ChallengesEnv, id: string, op: string, player: { id: string; fullName: string; dev?: boolean }, args: Record<string, unknown> = {}) =>
  stub(env, id).fetch("https://challenge/op", { method: "POST", body: JSON.stringify({ op, player, args }) });

export async function handleChallenges(req: Request, env: ChallengesEnv, url: URL): Promise<Response | null> {
  const path = url.pathname;
  if (path !== "/challenges" && !path.startsWith("/challenges/")) return null;
  const me = await authenticate(env, req);
  if (!me) return fail(401, "sign in first");
  const row = await env.DB.prepare("SELECT name, tag, dev FROM players WHERE player_id = ?1").bind(me).first<{ name: string; tag: string; dev: number }>();
  if (!row) return fail(404, "not registered");
  const player = { id: me, fullName: `${row.name}#${row.tag}`, dev: !!row.dev };
  const body = req.method === "POST" ? await req.json().catch(() => ({})) as Record<string, unknown> : {};

  if (path === "/challenges" && req.method === "POST") {
    const open = await env.DB.prepare("SELECT count(*) AS n FROM challenge_players WHERE player_id = ?1").bind(me).first<{ n: number }>();
    if ((open?.n ?? 0) >= MAX_OPEN) return fail(409, "too many open challenges");
    const id = crypto.randomUUID();
    return call(env, id, "create", player, { ...body, id });
  }

  if (path === "/challenges" && req.method === "GET") {
    const ids = (await env.DB.prepare("SELECT challenge_id FROM challenge_players WHERE player_id = ?1 ORDER BY created DESC LIMIT ?2").bind(me, MAX_OPEN).all<{ challenge_id: string }>()).results;
    const list: unknown[] = [];
    for (const r of ids) {
      const res = await call(env, r.challenge_id, "get", player);
      if (res.ok) list.push((await res.json() as { challenge: unknown }).challenge);
      else if (res.status === 404) await env.DB.prepare("DELETE FROM challenge_players WHERE challenge_id = ?1").bind(r.challenge_id).run();   // gone: forget it
    }
    return Response.json({ challenges: list });
  }

  const m = path.match(/^\/challenges\/([0-9a-f-]{36})(?:\/([a-z]+))?$/);
  if (!m || !ID.test(m[1])) return fail(400, "bad challenge id");
  const [, id, op] = m;

  if (!op && req.method === "GET") return call(env, id, "get", player);
  if (op && req.method === "POST" && OPS.includes(op)) {
    if (op === "join") {
      const peek = await call(env, id, "peek", player);
      if (!peek.ok) return peek;
      const { owner } = await peek.json() as { owner: string };
      if (owner !== me) {
        const [a, b] = owner < me ? [owner, me] : [me, owner];
        const friends = await env.DB.prepare("SELECT 1 FROM friendships WHERE a = ?1 AND b = ?2 AND status = 'accepted'").bind(a, b).first();
        const blocked = await env.DB.prepare("SELECT 1 FROM blocks WHERE (blocker = ?1 AND blocked = ?2) OR (blocker = ?2 AND blocked = ?1)").bind(owner, me).first();
        if (!friends || blocked) return fail(403, "only friends of the owner can join");
      }
    }
    return call(env, id, op, player, body);
  }
  return fail(404, "not found");
}
