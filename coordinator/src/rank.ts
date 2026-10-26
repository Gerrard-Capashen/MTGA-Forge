/**
 * Rank: pips per ranked match, Mythic leaderboard by rating (migrations/0006_ratings.sql, docs/results-rank-mastery.md section 2).
 *
 *   GET /me/rank   (Bearer) -> {season, seasonStart, seasonEnd, ladder: [{class, level, steps}], pools: {constructed: {...}, limited: {...}}}
 *
 * The ladder is the client's: classes Bronze..Diamond with 4 levels each (4 = lowest), Mythic is a leaderboard place. Pips per level:
 * Bronze 4, Silver..Diamond 6. A win is +1 pip (+2 in Bronze), a loss -1 (nothing in Bronze, and never below level 4 of the class:
 * loss protection), a draw nothing. Filling the last pip of Diamond 1 reaches Mythic, which is never left for the season.
 * Every player also has an Elo rating from the first ranked match (K = 32, start 1500): it orders the Mythic leaderboard.
 *
 * Applied from the match verdict (results.ts settle, also the Room alarm path), exactly once per (match, player):
 *   verified: both players; forfeit: the winner and the leaver (the leaver takes the loss); conceded: the loser only (own admission).
 *   disputed / pending / void: nothing. A later revision of the verdict never undoes an applied event; it only adds the missing player's.
 * Only matches the queue paired as ranked count (match_seats), never a report's own pool. A match shorter than the minimum length or
 * past the daily cap for that pair of accounts is recorded with no change ('short' / 'pair-cap').
 */
import { authenticate, type AccountEnv } from "./accounts";

export interface RankEnv extends AccountEnv {
  /** Test override of the minimum ranked match length (ms) and of the per-pair daily cap. */
  RESULT_MIN_MS?: string;
  RANK_PAIR_CAP?: string;
}

export const SEASON = 1;
const SEASON_START = "2026-10-01T00:00:00Z";
const SEASON_END = "2099-01-01T00:00:00Z";

export const CLASSES = ["Bronze", "Silver", "Gold", "Platinum", "Diamond", "Mythic"] as const;
const MYTHIC = 5;
const LEVELS = 4;
const START_RATING = 1500;
const K = 32;
/** A ranked match must have run this long (both seats in the room until the result) to move anything. */
const MIN_RANKED_MS = 90_000;
/** Applied results per ordered pair of accounts per UTC day. */
const PAIR_CAP = 3;

export const stepsFor = (cls: number): number => (cls === 0 ? 4 : cls >= MYTHIC ? 0 : 6);

/** The ladder the client asks for (RankGetSeasonAndRankDetails.constructedRankInfo). */
export function ladder(): { class: string; level: number; steps: number }[] {
  const out: { class: string; level: number; steps: number }[] = [];
  for (let c = 0; c < MYTHIC; c++) for (let l = LEVELS; l >= 1; l--) out.push({ class: CLASSES[c], level: l, steps: stepsFor(c) });
  out.push({ class: "Mythic", level: 1, steps: 0 });
  return out;
}

/** The ladder a ranked queue counts for, null if the queue is not ranked. Premier Draft matches count for the Limited (Draft) ladder. */
export function poolOf(queue: string): string | null {
  if (/^RankedPlay_[A-Za-z0-9]+$/.test(queue)) return "constructed";
  return /^PremierDraft_[A-Za-z0-9]+$/.test(queue) ? "limited" : null;
}

export type Result = "win" | "loss" | "draw";
export interface Pips { cls: number; level: number; step: number }

/** The pips after one result (pure). */
export function nextPips(p: Pips, result: Result): Pips & { protectedLoss: boolean } {
  let { cls, level, step } = p;
  if (cls >= MYTHIC || result === "draw") return { cls, level, step, protectedLoss: false };
  if (result === "win") {
    step += cls === 0 ? 2 : 1;
    while (cls < MYTHIC && step >= stepsFor(cls)) {
      step -= stepsFor(cls);
      if (--level === 0) { cls++; level = LEVELS; }
      if (cls === MYTHIC) { level = 1; step = 0; }
    }
    return { cls, level, step, protectedLoss: false };
  }
  if (cls === 0) return { cls, level, step, protectedLoss: true };       // Bronze never loses pips
  if (step > 0) return { cls, level, step: step - 1, protectedLoss: false };
  if (level < LEVELS) return { cls, level: level + 1, step: stepsFor(cls) - 1, protectedLoss: false };
  return { cls, level, step, protectedLoss: true };                      // floor of the class
}

export function elo(mine: number, theirs: number, result: Result): number {
  const expected = 1 / (1 + Math.pow(10, (theirs - mine) / 400));
  const score = result === "win" ? 1 : result === "loss" ? 0 : 0.5;
  return Math.round((mine + K * (score - expected)) * 100) / 100;
}

interface Row { player_id: string; class: number; level: number; step: number; wins: number; losses: number; draws: number; rating: number; mythic_at: number | null; version: number }
interface Seat { role: string; player_id: string; queue: string; pool: string }

const num = (v: string | undefined, d: number) => { const n = Number(v); return v !== undefined && v !== "" && Number.isFinite(n) && n >= 0 ? n : d; };

/** The queue calls this when it pairs two signed-in players in a ranked queue. */
export async function recordSeats(env: { DB: D1Database }, match: string, queue: string, host: string, guest: string): Promise<void> {
  const pool = poolOf(queue);
  if (!pool) return;
  const now = Date.now();
  const ins = "INSERT OR IGNORE INTO match_seats (match_id, role, player_id, queue, pool, created) VALUES (?1, ?2, ?3, ?4, ?5, ?6)";
  await env.DB.batch([env.DB.prepare(ins).bind(match, "host", host, queue, pool, now), env.DB.prepare(ins).bind(match, "guest", guest, queue, pool, now)]);
}

export async function seatsOf(env: { DB: D1Database }, match: string): Promise<Seat[]> {
  return (await env.DB.prepare("SELECT role, player_id, queue, pool FROM match_seats WHERE match_id = ?1").bind(match).all<Seat>()).results;
}

async function rowOf(env: RankEnv, player: string, pool: string): Promise<Row> {
  await env.DB.prepare("INSERT OR IGNORE INTO ratings (player_id, pool, season, updated) VALUES (?1, ?2, ?3, ?4)").bind(player, pool, SEASON, Date.now()).run();
  return (await env.DB.prepare("SELECT player_id, class, level, step, wins, losses, draws, rating, mythic_at, version FROM ratings WHERE player_id = ?1 AND pool = ?2 AND season = ?3")
    .bind(player, pool, SEASON).first<Row>())!;
}

/** Applies a settled ranked match to both players' ratings (idempotent; a no-op for unranked matches and for verdicts that move nothing). */
export async function applyRank(env: RankEnv, match: string): Promise<void> {
  const seats = await seatsOf(env, match);
  const host = seats.find(s => s.role === "host"), guest = seats.find(s => s.role === "guest");
  if (!host || !guest || host.player_id === guest.player_id) return;
  const verdict = await env.DB.prepare("SELECT status, winner FROM matches WHERE match_id = ?1").bind(match).first<{ status: string; winner: string | null }>();
  if (!verdict) return;

  const results: Partial<Record<"host" | "guest", Result>> = {};
  const reports = (await env.DB.prepare("SELECT role, outcome, created FROM match_reports WHERE match_id = ?1").bind(match).all<{ role: string; outcome: string; created: number }>()).results;
  if (verdict.status === "verified" || verdict.status === "forfeit") {
    const w = verdict.winner;
    if (w === "draw") { results.host = "draw"; results.guest = "draw"; }
    else if (w === "host" || w === "guest") { results[w] = "win"; results[w === "host" ? "guest" : "host"] = "loss"; }
  } else if (verdict.status === "conceded") {
    const loser = reports.find(r => r.outcome === "loss");
    if (loser && (loser.role === "host" || loser.role === "guest")) results[loser.role] = "loss";
  }
  const roles = (["host", "guest"] as const).filter(r => results[r]);
  if (roles.length === 0) return;

  const done = (await env.DB.prepare("SELECT player_id FROM rating_events WHERE match_id = ?1").bind(match).all<{ player_id: string }>()).results.map(r => r.player_id);
  const todo = roles.filter(r => !done.includes((r === "host" ? host : guest).player_id));
  if (todo.length === 0) return;

  // match length: from both seats in the room to the last report (verified/conceded) or to the drop (forfeit)
  const room = await env.DB.prepare("SELECT started, left_at FROM match_rooms WHERE match_id = ?1").bind(match).first<{ started: number; left_at: number | null }>();
  const end = verdict.status === "forfeit" ? room?.left_at ?? 0 : Math.max(0, ...reports.map(r => r.created));
  const short = !room || end - room.started < num(env.RESULT_MIN_MS, MIN_RANKED_MS);

  // both ratings as they were before this match (Elo uses the opponent's pre-match number)
  const before = { host: await rowOf(env, host.player_id, host.pool), guest: await rowOf(env, guest.player_id, guest.pool) };
  for (const role of todo) {
    const me = role === "host" ? host : guest, opp = role === "host" ? guest : host;
    await applyOne(env, match, me, opp.player_id, results[role]!, verdict.status, short, before[role === "host" ? "guest" : "host"].rating);
  }
}

async function applyOne(env: RankEnv, match: string, me: Seat, opponent: string, result: Result, status: string, short: boolean, oppRating: number): Promise<void> {
  const dayStart = Math.floor(Date.now() / 86_400_000) * 86_400_000;
  for (let attempt = 0; attempt < 5; attempt++) {
    const exists = await env.DB.prepare("SELECT 1 FROM rating_events WHERE match_id = ?1 AND player_id = ?2").bind(match, me.player_id).first();
    if (exists) return;
    const row = await rowOf(env, me.player_id, me.pool);
    let reason = short ? "short" : "applied";
    if (reason === "applied") {
      const n = await env.DB.prepare("SELECT COUNT(*) AS n FROM rating_events WHERE player_id = ?1 AND opponent_id = ?2 AND created >= ?3 AND reason = 'applied'")
        .bind(me.player_id, opponent, dayStart).first<{ n: number }>();
      if ((n?.n ?? 0) >= num(env.RANK_PAIR_CAP, PAIR_CAP)) reason = "pair-cap";
    }
    const applied = reason === "applied";
    const next = applied ? nextPips({ cls: row.class, level: row.level, step: row.step }, result) : { cls: row.class, level: row.level, step: row.step, protectedLoss: false };
    const rating = applied ? elo(row.rating, oppRating, result) : row.rating;
    const now = Date.now();
    const mythicAt = row.mythic_at ?? (next.cls >= MYTHIC ? now : null);
    const w = applied && result === "win" ? 1 : 0, l = applied && result === "loss" ? 1 : 0, d = applied && result === "draw" ? 1 : 0;
    try {
      await env.DB.batch([
        env.DB.prepare(`UPDATE ratings SET class = ?1, level = ?2, step = ?3, wins = wins + ?4, losses = losses + ?5, draws = draws + ?6, rating = ?7,
            mythic_at = ?8, version = version + 1, last_match = ?14, updated = ?9 WHERE player_id = ?10 AND pool = ?11 AND season = ?12 AND version = ?13`)
          .bind(next.cls, next.level, next.step, w, l, d, rating, mythicAt, now, me.player_id, me.pool, SEASON, row.version, match),
        env.DB.prepare(`INSERT INTO rating_events (match_id, player_id, opponent_id, pool, season, queue, result, status, reason, old_class, old_level, old_step,
            new_class, new_level, new_step, protected, old_rating, new_rating, seq, created)
            VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16, ?17, ?18,
              (SELECT version FROM ratings WHERE player_id = ?2 AND pool = ?4 AND season = ?5 AND version = ?19 AND last_match = ?1), ?20)`)
          .bind(match, me.player_id, opponent, me.pool, SEASON, me.queue, result, status, reason, row.class, row.level, row.step,
            next.cls, next.level, next.step, applied && next.protectedLoss ? 1 : 0, row.rating, rating, row.version + 1, now),
      ]);
      return;
    } catch {
      // the event exists already (another settle won) or the row moved under us (another match of this player): look again
    }
  }
}

/** The leaderboard place (1 = best) and percentile of a Mythic player, 0/0 below Mythic. */
async function placeOf(env: RankEnv, row: { class: number; rating: number; mythic_at: number | null }, pool: string): Promise<{ place: number; percentile: number }> {
  if (row.class < MYTHIC) return { place: 0, percentile: 0 };
  const r = await env.DB.prepare(`SELECT
        (SELECT COUNT(*) FROM ratings WHERE pool = ?1 AND season = ?2 AND class = ?3 AND (rating > ?4 OR (rating = ?4 AND mythic_at < ?5))) AS above,
        (SELECT COUNT(*) FROM ratings WHERE pool = ?1 AND season = ?2 AND class = ?3) AS total`)
    .bind(pool, SEASON, MYTHIC, row.rating, row.mythic_at ?? 0).first<{ above: number; total: number }>();
  const place = (r?.above ?? 0) + 1, total = Math.max(1, r?.total ?? 1);
  return { place, percentile: Math.round((place / total) * 1000) / 10 };
}

const pipsJson = (cls: number, level: number, step: number) => ({ class: CLASSES[cls] ?? "Bronze", level, step });

/** This player's rank event for a match, for the end screen (null when none was applied). */
export async function rankUpdateFor(env: RankEnv, match: string, player: string): Promise<Record<string, unknown> | null> {
  const e = await env.DB.prepare("SELECT * FROM rating_events WHERE match_id = ?1 AND player_id = ?2").bind(match, player)
    .first<Record<string, number & string>>();
  if (!e) return null;
  const row = await env.DB.prepare("SELECT class, rating, mythic_at FROM ratings WHERE player_id = ?1 AND pool = ?2 AND season = ?3").bind(player, e.pool, e.season)
    .first<{ class: number; rating: number; mythic_at: number | null }>();
  const now = row ? await placeOf(env, row, e.pool) : { place: 0, percentile: 0 };
  return {
    season: e.season, pool: e.pool, result: e.result, reason: e.reason, lossProtected: e.protected === 1,
    old: pipsJson(e.old_class, e.old_level, e.old_step), new: pipsJson(e.new_class, e.new_level, e.new_step),
    oldRating: e.old_rating, newRating: e.new_rating, place: now.place, percentile: now.percentile,
  };
}

async function poolJson(env: RankEnv, player: string, pool: string) {
  const row = await env.DB.prepare("SELECT class, level, step, wins, losses, draws, rating, mythic_at FROM ratings WHERE player_id = ?1 AND pool = ?2 AND season = ?3")
    .bind(player, pool, SEASON).first<{ class: number; level: number; step: number; wins: number; losses: number; draws: number; rating: number; mythic_at: number | null }>();
  const r = row ?? { class: 0, level: LEVELS, step: 0, wins: 0, losses: 0, draws: 0, rating: START_RATING, mythic_at: null };
  const p = await placeOf(env, r, pool);
  return { ...pipsJson(r.class, r.level, r.step), wins: r.wins, losses: r.losses, draws: r.draws, rating: r.rating, place: p.place, percentile: p.percentile };
}

export async function handleRank(req: Request, env: RankEnv, url: URL): Promise<Response | null> {
  if (url.pathname !== "/me/rank") return null;
  if (req.method !== "GET") return Response.json({ error: "not found" }, { status: 404 });
  const me = await authenticate(env, req);
  if (!me) return Response.json({ error: "sign in first" }, { status: 401 });
  return Response.json({
    season: SEASON, seasonStart: SEASON_START, seasonEnd: SEASON_END, ladder: ladder(),
    pools: { constructed: await poolJson(env, me, "constructed"), limited: await poolJson(env, me, "limited") },
  });
}
