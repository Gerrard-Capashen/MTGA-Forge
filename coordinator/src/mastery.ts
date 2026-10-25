/**
 * Mastery: one endless XP track; every level gives one cosmetic (migrations/0007_mastery.sql, docs/results-rank-mastery.md section 3).
 *
 *   GET  /mastery/track   the track: {version, items: [[kind, cosmetic id], ...]}, level n gives items[2n-2] and items[2n-1] (public, the same for everybody)
 *   GET  /me/mastery      (Bearer) -> {xp, level, levelXp, dailyCap, todayXp, trackVersion, owned: {avatars, sleeves, pets, emotes, titles}}
 *   POST /me/xp/bot {match}   (Bearer) -> {gained, ...as /me/mastery}: a local bot match finished, BOT_XP once per match id (cannot be verified: cosmetic only, inside the daily cap)
 *
 * XP of online matches is applied from the match verdict (results.ts settle, also the Room alarm path), once per (match, player), paying for
 * PLAYING: verified = MATCH_XP to both players + WIN_XP to the winner; forfeit = the winner's MATCH_XP + WIN_XP (the leaver gets nothing);
 * conceded = MATCH_XP to the player who reported the loss; disputed / pending / void = nothing. Everything inside a daily cap (UTC day).
 * A level-up writes the level's two cosmetics into cosmetic_grants in the same batch. The always-owned set (the Adventurer, the default card
 * back, the basic phrases) is not stored: the Java server adds it. The champion title is the track's last level.
 */
import { authenticate, type AccountEnv } from "./accounts";
import track from "./mastery-track.json";

export interface MasteryEnv extends AccountEnv {
  /** Test override of the daily XP cap. */
  MASTERY_DAILY_CAP?: string;
}

export const LEVEL_XP = 1_000;
export const MATCH_XP = 250;
export const WIN_XP = 150;
export const BOT_XP = 100;
/** Cosmetics per level: the client's two reward rows (level n gives items 2n-2 and 2n-1). */
export const PER_LEVEL = 2;
const DAILY_CAP = 3_000;

const ITEMS = (track as { version: number; items: [string, string][] }).items;
const LISTS: Record<string, string> = { avatar: "avatars", sleeve: "sleeves", pet: "pets", emote: "emotes", title: "titles" };
const MATCH = /^[A-Za-z0-9_.:\-]{1,80}$/;

const num = (v: string | undefined, d: number) => { const n = Number(v); return v !== undefined && v !== "" && Number.isFinite(n) && n >= 0 ? n : d; };
const fail = (status: number, error: string) => Response.json({ error }, { status });
const dayOf = (t: number) => Math.floor(t / 86_400_000);

interface Row { xp: number; level: number; day: number; day_xp: number; version: number }

async function rowOf(env: MasteryEnv, player: string): Promise<Row> {
  await env.DB.prepare("INSERT OR IGNORE INTO progress (player_id, updated) VALUES (?1, ?2)").bind(player, Date.now()).run();
  return (await env.DB.prepare("SELECT xp, level, day, day_xp, version FROM progress WHERE player_id = ?1").bind(player).first<Row>())!;
}

/** Pays `asked` XP to a player once per award key (inside the daily cap) and grants the cosmetics of the levels it finishes. */
export async function award(env: MasteryEnv, key: string, player: string, source: string, asked: number): Promise<void> {
  for (let attempt = 0; attempt < 5; attempt++) {
    if (await env.DB.prepare("SELECT 1 FROM xp_events WHERE award = ?1 AND player_id = ?2").bind(key, player).first()) return;
    const row = await rowOf(env, player);
    const now = Date.now(), today = dayOf(now);
    const before = row.day === today ? row.day_xp : 0;
    const pay = Math.max(0, Math.min(asked, num(env.MASTERY_DAILY_CAP, DAILY_CAP) - before));
    const xp = row.xp + pay, level = Math.floor(xp / LEVEL_XP);
    const stmts = [
      env.DB.prepare(`UPDATE progress SET xp = ?1, level = ?2, day = ?3, day_xp = ?4, version = version + 1, last_award = ?5, updated = ?6
          WHERE player_id = ?7 AND version = ?8`).bind(xp, level, today, before + pay, key, now, player, row.version),
      env.DB.prepare(`INSERT INTO xp_events (award, player_id, source, asked, xp, seq, created)
          VALUES (?1, ?2, ?3, ?4, ?5, (SELECT version FROM progress WHERE player_id = ?2 AND version = ?6 AND last_award = ?1), ?7)`)
        .bind(key, player, source, asked, pay, row.version + 1, now),
    ];
    for (let l = row.level + 1; l <= level && (l - 1) * PER_LEVEL < ITEMS.length; l++) {
      for (const [kind, id] of ITEMS.slice((l - 1) * PER_LEVEL, l * PER_LEVEL)) {
        stmts.push(env.DB.prepare("INSERT OR IGNORE INTO cosmetic_grants (player_id, cosmetic_id, kind, source, granted_at) VALUES (?1, ?2, ?3, ?4, ?5)")
          .bind(player, id, kind, `mastery:${l}`, now));
      }
    }
    try {
      await env.DB.batch(stmts);
      return;
    } catch {
      // the award exists already (another settle won) or the row moved under us (another award of this player): look again
    }
  }
}

/** Pays the XP of a settled online match (idempotent; nothing for verdicts that pay nothing). */
export async function applyMastery(env: MasteryEnv, match: string): Promise<void> {
  const verdict = await env.DB.prepare("SELECT status, winner FROM matches WHERE match_id = ?1").bind(match).first<{ status: string; winner: string | null }>();
  if (!verdict) return;
  const reports = (await env.DB.prepare("SELECT role, player_id, outcome FROM match_reports WHERE match_id = ?1").bind(match)
    .all<{ role: string; player_id: string; outcome: string }>()).results;
  if (reports.length === 2 && reports[0].player_id === reports[1].player_id) return;      // one account in both seats (tests only)
  for (const r of reports) {
    let xp = 0;
    if (verdict.status === "verified") xp = MATCH_XP + (verdict.winner === r.role ? WIN_XP : 0);
    else if (verdict.status === "forfeit" && verdict.winner === r.role) xp = MATCH_XP + WIN_XP;
    else if (verdict.status === "conceded" && r.outcome === "loss") xp = MATCH_XP;
    if (xp > 0) await award(env, match, r.player_id, "match", xp);
  }
}

/** This player's XP for a match, for the end of the match (null when none was paid yet). */
export async function masteryFor(env: MasteryEnv, match: string, player: string): Promise<{ xp: number; asked: number } | null> {
  const e = await env.DB.prepare("SELECT xp, asked FROM xp_events WHERE award = ?1 AND player_id = ?2").bind(match, player).first<{ xp: number; asked: number }>();
  return e ? { xp: e.xp, asked: e.asked } : null;
}

async function me(env: MasteryEnv, player: string) {
  const row = await env.DB.prepare("SELECT xp, level, day, day_xp FROM progress WHERE player_id = ?1").bind(player).first<Row>()
    ?? { xp: 0, level: 0, day: 0, day_xp: 0, version: 0 };
  const owned: Record<string, string[]> = { avatars: [], sleeves: [], pets: [], emotes: [], titles: [] };
  const grants = (await env.DB.prepare("SELECT cosmetic_id, kind FROM cosmetic_grants WHERE player_id = ?1 ORDER BY granted_at, cosmetic_id").bind(player)
    .all<{ cosmetic_id: string; kind: string }>()).results;
  for (const g of grants) owned[LISTS[g.kind] ?? "other"]?.push(g.cosmetic_id);
  return {
    xp: row.xp, level: row.level, levelXp: LEVEL_XP, dailyCap: num(env.MASTERY_DAILY_CAP, DAILY_CAP),
    todayXp: row.day === dayOf(Date.now()) ? row.day_xp : 0, trackVersion: (track as { version: number }).version, owned,
  };
}

export async function handleMastery(req: Request, env: MasteryEnv, url: URL): Promise<Response | null> {
  if (url.pathname === "/mastery/track" && req.method === "GET") {
    return new Response(JSON.stringify(track), { headers: { "content-type": "application/json", "cache-control": "public, max-age=3600" } });
  }
  if (url.pathname !== "/me/mastery" && url.pathname !== "/me/xp/bot") return null;
  const player = await authenticate(env, req);
  if (!player) return fail(401, "sign in first");
  if (url.pathname === "/me/mastery") return req.method === "GET" ? Response.json(await me(env, player)) : fail(404, "not found");
  if (req.method !== "POST") return fail(404, "not found");
  const body = await req.json().catch(() => ({})) as Record<string, unknown>;
  if (typeof body.match !== "string" || !MATCH.test(body.match)) return fail(400, "bad match id");
  const key = "bot:" + body.match;
  await award(env, key, player, "bot", BOT_XP);
  const paid = await env.DB.prepare("SELECT xp FROM xp_events WHERE award = ?1 AND player_id = ?2").bind(key, player).first<{ xp: number }>();
  return Response.json({ gained: paid?.xp ?? 0, ...(await me(env, player)) });
}
