/**
 * Daily quests (migrations/0008_quests.sql). Three slots per player, Arena-like: a slot keeps its
 * quest until it is finished, a finished quest is replaced on the next UTC day, each quest can be swapped once a day. A finished quest pays
 * its mastery XP (mastery.ts award, key "quest:<quest id>", so once).
 *
 *   GET  /me/quests                  (Bearer) -> {quests: [{questId, kind, goal, progress, xp, canSwap, done}]}
 *   POST /me/quests/swap {questId}   (Bearer) -> the same; 409 when that quest cannot be swapped (finished, or swapped today)
 *   POST /me/quests/progress {match, metrics: {name: count}}   (Bearer) -> {updates: [{questId, kind, goal, from, to, xp?}], quests: [...]}
 *        once per match id; `metrics` are the player's own counters of that match (the player's Forge server: its engine counted them, the host's
 *        engine for a guest) plus match counters (MatchPlayed, MatchWon, MatchPlayed_<Format>, MatchWon_<Format>, RankedPlayed, DraftPlayed).
 *        Trusted like the rest of the cosmetics (a modified client can fake them; at most three quests a day are at stake).
 */
import { authenticate, type AccountEnv } from "./accounts";
import { award } from "./mastery";

/** id = the client's text key Forge/Quest/<id>; metric = the counter it adds up. */
export const POOL: { id: string; metric: string; goal: number; xp: number }[] = [
  { id: "PlayVintage", metric: "MatchPlayed_Vintage", goal: 1, xp: 500 },
  { id: "PlayLegacy", metric: "MatchPlayed_Legacy", goal: 1, xp: 500 },
  { id: "PlayPauper", metric: "MatchPlayed_Pauper", goal: 2, xp: 500 },
  { id: "PlayPremodern", metric: "MatchPlayed_Premodern", goal: 2, xp: 500 },
  { id: "PlayModern", metric: "MatchPlayed_Modern", goal: 2, xp: 500 },
  { id: "PlayPioneer", metric: "MatchPlayed_Pioneer", goal: 2, xp: 500 },
  { id: "WinStandard", metric: "MatchWon_Standard", goal: 1, xp: 500 },
  { id: "PlayMomir", metric: "MatchPlayed_Momir", goal: 2, xp: 500 },
  { id: "PlayRanked", metric: "RankedPlayed", goal: 2, xp: 750 },
  { id: "PlayDraft", metric: "DraftPlayed", goal: 2, xp: 750 },
  { id: "AttackCreatures", metric: "CreaturesAttacked", goal: 20, xp: 500 },
  { id: "CastWhiteSpells", metric: "WhiteSpellsCast", goal: 20, xp: 500 },
  { id: "CastBlueSpells", metric: "BlueSpellsCast", goal: 20, xp: 500 },
  { id: "CastBlackSpells", metric: "BlackSpellsCast", goal: 20, xp: 500 },
  { id: "CastRedSpells", metric: "RedSpellsCast", goal: 20, xp: 500 },
  { id: "CastGreenSpells", metric: "GreenSpellsCast", goal: 20, xp: 500 },
  { id: "CastInstantsSorceries", metric: "InstantsAndSorceriesCast", goal: 15, xp: 500 },
  { id: "CastCreatures", metric: "CreaturesCast", goal: 20, xp: 500 },
  { id: "PlayLands", metric: "LandsPlayed", goal: 25, xp: 500 },
  { id: "DealDamage", metric: "DamageDealt", goal: 40, xp: 500 },
  { id: "KillCreatures", metric: "OpponentCreaturesDied", goal: 15, xp: 750 },
  { id: "CastArtifactsEnchantments", metric: "ArtifactsAndEnchantmentsCast", goal: 10, xp: 500 },
];
const SLOTS = 3;
const MATCH = /^[A-Za-z0-9_.:\-]{1,80}$/;
const METRIC = /^[A-Za-z_]{1,64}$/;

interface Row { slot: number; quest_id: string; kind: string; goal: number; progress: number; xp: number; assigned_day: number; swap_day: number; done_day: number | null }

const fail = (status: number, error: string) => Response.json({ error }, { status });
const dayOf = (t: number) => Math.floor(t / 86_400_000);

async function rows(env: AccountEnv, player: string): Promise<Row[]> {
  return (await env.DB.prepare("SELECT slot, quest_id, kind, goal, progress, xp, assigned_day, swap_day, done_day FROM quests WHERE player_id = ?1 ORDER BY slot")
    .bind(player).all<Row>()).results;
}

/** A quest kind none of `taken` is. */
function pick(taken: string[]): (typeof POOL)[number] {
  const free = POOL.filter(q => !taken.includes(q.id));
  return free[Math.floor(Math.random() * free.length)];
}

/** Fills empty slots and replaces the quests finished before today (idempotent for the day). */
async function refresh(env: AccountEnv, player: string): Promise<Row[]> {
  const today = dayOf(Date.now());
  let current = await rows(env, player);
  const stale = (r: Row | undefined) => !r || (r.done_day !== null && r.done_day < today);
  if (![0, 1, 2].slice(0, SLOTS).some(s => stale(current.find(r => r.slot === s)))) return current;
  const taken = current.filter(r => !stale(r)).map(r => r.kind);
  const stmts = [];
  for (let s = 0; s < SLOTS; s++) {
    const r = current.find(x => x.slot === s);
    if (!stale(r)) continue;
    const q = pick(taken);
    taken.push(q.id);
    // the WHERE keeps a concurrent refresh from replacing twice: only a row still in the state read here is replaced
    stmts.push(env.DB.prepare(`INSERT INTO quests (player_id, slot, quest_id, kind, goal, progress, xp, assigned_day, swap_day, done_day)
        VALUES (?1, ?2, ?3, ?4, ?5, 0, ?6, ?7, -1, NULL)
        ON CONFLICT(player_id, slot) DO UPDATE SET quest_id = ?3, kind = ?4, goal = ?5, progress = 0, xp = ?6, assigned_day = ?7, swap_day = -1, done_day = NULL
        WHERE quests.quest_id = ?8`)
      .bind(player, s, crypto.randomUUID(), q.id, q.goal, q.xp, today, r?.quest_id ?? ""));
  }
  await env.DB.batch(stmts);
  current = await rows(env, player);
  return current;
}

function json(rs: Row[]) {
  const today = dayOf(Date.now());
  return rs.map(r => ({ questId: r.quest_id, kind: r.kind, goal: r.goal, progress: r.progress, xp: r.xp, done: r.done_day !== null,
    canSwap: r.done_day === null && r.swap_day !== today }));
}

export async function handleQuests(req: Request, env: AccountEnv, url: URL): Promise<Response | null> {
  if (!url.pathname.startsWith("/me/quests")) return null;
  const player = await authenticate(env, req);
  if (!player) return fail(401, "sign in first");
  if (url.pathname === "/me/quests" && req.method === "GET") return Response.json({ quests: json(await refresh(env, player)) });
  if (req.method !== "POST") return fail(404, "not found");
  const body = await req.json().catch(() => ({})) as Record<string, unknown>;

  if (url.pathname === "/me/quests/swap") {
    const current = await refresh(env, player);
    const r = current.find(x => x.quest_id === body.questId);
    const today = dayOf(Date.now());
    if (!r) return fail(404, "no such quest");
    if (r.done_day !== null || r.swap_day === today) return fail(409, "this quest cannot be swapped today");
    const q = pick(current.map(x => x.kind));
    await env.DB.prepare(`UPDATE quests SET quest_id = ?1, kind = ?2, goal = ?3, progress = 0, xp = ?4, assigned_day = ?5, swap_day = ?5
        WHERE player_id = ?6 AND slot = ?7 AND quest_id = ?8 AND done_day IS NULL`)
      .bind(crypto.randomUUID(), q.id, q.goal, q.xp, today, player, r.slot, r.quest_id).run();
    return Response.json({ quests: json(await rows(env, player)) });
  }

  if (url.pathname === "/me/quests/progress") {
    if (typeof body.match !== "string" || !MATCH.test(body.match)) return fail(400, "bad match id");
    const metrics = (typeof body.metrics === "object" && body.metrics !== null ? body.metrics : {}) as Record<string, unknown>;
    const current = await refresh(env, player);
    const first = await env.DB.prepare("INSERT OR IGNORE INTO quest_events (award, player_id, created) VALUES (?1, ?2, ?3)")
      .bind(body.match, player, Date.now()).run();
    const updates: Record<string, unknown>[] = [];
    if ((first.meta?.changes ?? 0) > 0) {
      const today = dayOf(Date.now());
      for (const r of current) {
        if (r.done_day !== null) continue;
        const def = POOL.find(q => q.id === r.kind);
        const n = def && METRIC.test(def.metric) ? Number(metrics[def.metric]) : 0;
        if (!Number.isFinite(n) || n <= 0) continue;
        const to = Math.min(r.goal, r.progress + Math.floor(n));
        const done = to >= r.goal;
        await env.DB.prepare("UPDATE quests SET progress = ?1, done_day = ?2 WHERE quest_id = ?3 AND done_day IS NULL")
          .bind(to, done ? today : null, r.quest_id).run();
        if (done) await award(env, "quest:" + r.quest_id, player, "quest", r.xp);
        updates.push({ questId: r.quest_id, kind: r.kind, goal: r.goal, from: r.progress, to, ...(done ? { xp: r.xp } : {}) });
      }
    }
    return Response.json({ updates, quests: json(await rows(env, player)) });
  }
  return fail(404, "not found");
}
