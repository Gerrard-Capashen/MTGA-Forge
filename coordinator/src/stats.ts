/**
 * Public statistics for the mtgaforge.com/stats page: counts only, never a name, an id or anything that points at one player.
 *
 *   GET /stats -> {at, players: {total, new7d, active24h, active7d, active30d}, online: {total, Available, Busy, Away},
 *                  waiting: [{kind: "match"|"table", queue, n}], matches: {h24, d7, d30, byPool7d: [{pool, n}], perDay: [{day, n}]},
 *                  bots: {h24, d7}, rank: {season, classes: [{class, n}]}}
 *   CORS open (the page lives on another origin); cached for CACHE_S at the edge so a busy Discord link does not hit D1 each time.
 *
 * "waiting" = people in a public queue right now (friend-code queues are never reported). The Queue and TableQueue objects tell the
 * Hub their count whenever it changes (reportWaiting); a count not refreshed for STALE_MS is dropped.
 */
import { hubOf, type FriendsEnv } from "./friends";
import { CLASSES, SEASON } from "./rank";

const CACHE_S = 60;
const DAY = 86_400_000;
export const STALE_MS = 10 * 60_000;

export interface Waiting { kind: "match" | "table"; queue: string; n: number; at: number }

/** Tells the Hub how many people wait in one queue object (key = that object's id). Never throws. */
export async function reportWaiting(env: FriendsEnv, key: string, kind: Waiting["kind"], queue: string, n: number): Promise<void> {
  try {
    await hubOf(env).fetch("https://hub/waiting", { method: "POST", body: JSON.stringify({ key, kind, queue, n }) });
  } catch { /* statistics only */ }
}

export interface StatsEnv extends FriendsEnv {
  /** Test override (seconds, 0 = no cache), see package.json "dev". */
  STATS_CACHE_S?: string;
}

export async function handleStats(req: Request, env: StatsEnv, url: URL, ctx?: ExecutionContext): Promise<Response | null> {
  if (url.pathname !== "/stats") return null;
  const ttl = env.STATS_CACHE_S !== undefined && Number.isFinite(Number(env.STATS_CACHE_S)) ? Number(env.STATS_CACHE_S) : CACHE_S;
  const cors = { "access-control-allow-origin": "*", "cache-control": `public, max-age=${ttl}` };
  if (req.method === "OPTIONS") return new Response(null, { headers: { ...cors, "access-control-allow-methods": "GET" } });
  if (req.method !== "GET") return new Response("GET only", { status: 405 });

  const cache = (globalThis as unknown as { caches?: { default: Cache } }).caches?.default;
  const key = new Request(url.origin + "/stats");
  const hit = ttl > 0 ? await cache?.match(key) : undefined;
  if (hit) return hit;

  const res = Response.json(await compute(env), { headers: cors });
  if (cache && ttl > 0) {
    const put = cache.put(key, res.clone());
    if (ctx) ctx.waitUntil(put); else await put;
  }
  return res;
}

async function compute(env: FriendsEnv): Promise<Record<string, unknown>> {
  const now = Date.now();
  const today = Math.floor(now / DAY);
  const db = env.DB;
  const counted = "status <> 'void'";
  const [players, matches, byPool, perDay, bots, ranks] = await db.batch([
    db.prepare(`SELECT COUNT(*) AS total,
        SUM(created >= ?1) AS new7d, SUM(last_seen >= ?2) AS active24h, SUM(last_seen >= ?1) AS active7d, SUM(last_seen >= ?3) AS active30d
      FROM players WHERE status = 'ok'`).bind(now - 7 * DAY, now - DAY, now - 30 * DAY),
    db.prepare(`SELECT SUM(resolved >= ?1) AS h24, SUM(resolved >= ?2) AS d7, COUNT(*) AS d30 FROM matches WHERE ${counted} AND resolved >= ?3`)
      .bind(now - DAY, now - 7 * DAY, now - 30 * DAY),
    db.prepare(`SELECT pool, COUNT(*) AS n FROM matches WHERE ${counted} AND resolved >= ?1 GROUP BY pool ORDER BY n DESC LIMIT 20`).bind(now - 7 * DAY),
    db.prepare(`SELECT resolved / ${DAY} AS day, COUNT(*) AS n FROM matches WHERE ${counted} AND resolved >= ?1 GROUP BY day`).bind((today - 29) * DAY),
    db.prepare("SELECT SUM(created >= ?1) AS h24, COUNT(*) AS d7 FROM xp_events WHERE source = 'bot' AND created >= ?2").bind(now - DAY, now - 7 * DAY),
    db.prepare("SELECT class, COUNT(*) AS n FROM ratings WHERE pool = 'constructed' AND season = ?1 GROUP BY class").bind(SEASON),
  ]);
  const one = (r: D1Result) => (r.results[0] ?? {}) as Record<string, number | null>;
  const num = (v: unknown) => (typeof v === "number" ? v : 0);

  const p = one(players), m = one(matches), b = one(bots);
  const days = new Map((perDay.results as { day: number; n: number }[]).map(r => [r.day, r.n]));
  const classes = new Map((ranks.results as { class: number; n: number }[]).map(r => [r.class, r.n]));

  let hub: { online: Record<string, number>; waiting: Waiting[] } = { online: {}, waiting: [] };
  try { hub = await (await hubOf(env).fetch("https://hub/stats")).json(); } catch { /* the page shows what D1 has */ }
  const waiting = new Map<string, Omit<Waiting, "at">>();   // one queue may span several objects (card database versions)
  for (const w of hub.waiting) {
    const k = w.kind + "|" + w.queue;
    const x = waiting.get(k);
    if (x) x.n += w.n; else waiting.set(k, { kind: w.kind, queue: w.queue, n: w.n });
  }

  return {
    at: now,
    players: { total: num(p.total), new7d: num(p.new7d), active24h: num(p.active24h), active7d: num(p.active7d), active30d: num(p.active30d) },
    online: {
      total: num(hub.online.Available) + num(hub.online.Busy) + num(hub.online.Away),
      Available: num(hub.online.Available), Busy: num(hub.online.Busy), Away: num(hub.online.Away),
    },
    waiting: [...waiting.values()].filter(w => w.n > 0).sort((a, b) => b.n - a.n),
    matches: {
      h24: num(m.h24), d7: num(m.d7), d30: num(m.d30),
      byPool7d: byPool.results,
      perDay: Array.from({ length: 30 }, (_, i) => {
        const d = today - 29 + i;
        return { day: new Date(d * DAY).toISOString().slice(0, 10), n: days.get(d) ?? 0 };
      }),
    },
    bots: { h24: num(b.h24), d7: num(b.d7) },
    rank: { season: SEASON, classes: CLASSES.map((c, i) => ({ class: c, n: classes.get(i) ?? 0 })) },
  };
}
