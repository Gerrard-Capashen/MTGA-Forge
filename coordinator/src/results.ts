/**
 * Match results (REST, Bearer token). Both seats report their own outcome; the coordinator accepts a match only when the two agree.
 *
 *   POST /matches/{id}/result {role, token, outcome: "win"|"loss"|"draw", pool}
 *        `token` is the match token the seat got when it was paired (proves it sat in that match as that role), the Bearer token says who it is.
 *        Answers {status, winner?}. A report cannot be changed; repeating the same one is fine.
 *   GET  /matches/{id}    the verdict, for the two players of the match (this also settles a match whose other seat never reported);
 *        for a ranked match also `rank`: the caller's applied rank change (rank.ts rankUpdateFor), absent until there is one;
 *        `mastery` {xp, asked}: the caller's mastery XP for the match (mastery.ts), absent until paid.
 *
 * status: pending | verified | disputed | forfeit | conceded | void (migrations/0004_match_results.sql). Both seats reported and agree =
 * verified. One seat only: a reported loss is taken at its word (conceded), a reported win only when the relay room saw the opponent
 * drop for good after MIN_MATCH_MS (forfeit); anything else stays pending and ends void after PENDING_TTL_MS. A later report from the
 * missing seat upgrades conceded/forfeit to verified, or to disputed when it contradicts.
 * Ranked matches (paired by a ranked queue, rank.ts match_seats): only the seated account may report a seat, and every settle applies
 * the verdict to the ratings (rank.ts applyRank, idempotent).
 */
import { authenticate, type AccountEnv } from "./accounts";
import { applyMastery, masteryFor, type MasteryEnv } from "./mastery";
import { applyRank, rankUpdateFor, seatsOf, type RankEnv } from "./rank";
import { matchToken, type Role } from "./tokens";

export interface ResultsEnv extends AccountEnv, RankEnv, MasteryEnv {
  TOKEN_SECRET?: string;
  /** "1" lets one player id fill both seats (local tests with two clients on one profile); such a match is never verified for rewards otherwise. */
  ALLOW_SELF_PAIRING?: string;
  /** Milliseconds, test overrides of MIN_MATCH_MS / LEAVE_GRACE_MS. */
  RESULT_MIN_MS?: string;
  RESULT_GRACE_MS?: string;
}

const OUTCOMES = ["win", "loss", "draw"] as const;
type Outcome = typeof OUTCOMES[number];
const POOL = /^[A-Za-z0-9_.\-]{1,64}$/;
const ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

/** A forfeit win needs the match to have run at least this long (an alt that queues and drops at once earns nothing). */
const MIN_MATCH_MS = 90_000;
/** The opponent must have been gone this long without coming back (room for a reconnect). */
const LEAVE_GRACE_MS = 120_000;
/** A match with no usable result is void after this. */
const PENDING_TTL_MS = 24 * 3_600_000;

interface Report { role: Role; player_id: string; outcome: Outcome; pool: string; created: number }
interface RoomRow { started: number; left_role: Role | null; left_at: number | null; rejoined: number | null }
interface Verdict { status: string; winner?: string }

const fail = (status: number, error: string) => Response.json({ error }, { status });
const opposite: Record<Outcome, Outcome> = { win: "loss", loss: "win", draw: "draw" };
const otherRole = (r: Role): Role => (r === "host" ? "guest" : "host");
const ms = (v: string | undefined, d: number) => { const n = Number(v); return v !== undefined && v !== "" && Number.isFinite(n) && n >= 0 ? n : d; };

/** The relay room calls these (index.ts Room): the first time both seats are in, and the first seat to drop. */
export async function roomStarted(env: { DB: D1Database }, match: string): Promise<void> {
  await env.DB.prepare("INSERT OR IGNORE INTO match_rooms (match_id, started) VALUES (?1, ?2)").bind(match, Date.now()).run();
}
/** How long a seat must have been gone before the other seat's win counts as a forfeit. */
export const leaveGraceMs = (env: ResultsEnv): number => ms(env.RESULT_GRACE_MS, LEAVE_GRACE_MS);

/** Settles a match by itself (the room's timer): whatever the reports and the room's record say by now. */
export async function settleMatch(env: ResultsEnv, id: string): Promise<void> {
  const reports = await reportsOf(env, id);
  if (reports.length > 0) await settle(env, id, reports);
}

export async function roomLeft(env: { DB: D1Database }, match: string, role: Role): Promise<void> {
  await env.DB.prepare("UPDATE match_rooms SET left_role = ?2, left_at = ?3 WHERE match_id = ?1 AND left_at IS NULL").bind(match, role, Date.now()).run();
}

export async function handleResults(req: Request, env: ResultsEnv, url: URL): Promise<Response | null> {
  const m = url.pathname.match(/^\/matches\/([0-9a-f-]{36})(\/result)?$/);
  if (!m) return null;
  const [, id, isResult] = m;
  if (!ID.test(id)) return fail(400, "bad match id");
  const me = await authenticate(env, req);
  if (!me) return fail(401, "sign in first");

  if (!isResult && req.method === "GET") {
    const reports = await reportsOf(env, id);
    const seats = await seatsOf(env, id);
    if (!reports.some(r => r.player_id === me) && !seats.some(s => s.player_id === me)) return fail(404, "not your match");
    const verdict: Verdict & { rank?: unknown; mastery?: unknown } = reports.length > 0 ? await settle(env, id, reports) : { status: "pending" };
    const rank = seats.length > 0 ? await rankUpdateFor(env, id, me) : null;
    if (rank) verdict.rank = rank;
    const xp = await masteryFor(env, id, me);
    if (xp) verdict.mastery = xp;
    return Response.json(verdict);
  }
  if (!isResult || req.method !== "POST") return fail(404, "not found");

  const body = await req.json().catch(() => ({})) as Record<string, unknown>;
  const role = body.role, outcome = body.outcome, pool = body.pool;
  if (role !== "host" && role !== "guest") return fail(400, "bad role");
  if (typeof outcome !== "string" || !(OUTCOMES as readonly string[]).includes(outcome)) return fail(400, "bad outcome");
  if (typeof pool !== "string" || !POOL.test(pool)) return fail(400, "bad pool");
  if (typeof body.token !== "string" || body.token !== await matchToken(env, id, role)) return fail(403, "bad token");
  const seat = (await seatsOf(env, id)).find(s => s.role === role);
  if (seat && seat.player_id !== me) return fail(403, "not your seat");          // ranked: the queue knows who sat there

  const before = await reportsOf(env, id);
  const mine = before.find(r => r.role === role);
  if (mine) {
    if (mine.player_id !== me || mine.outcome !== outcome) return fail(409, "this seat already reported");
  } else {
    const theirs = before.find(r => r.role !== role);
    if (theirs && theirs.player_id === me && env.ALLOW_SELF_PAIRING !== "1") return fail(409, "one player cannot fill both seats");
    await env.DB.prepare("INSERT OR IGNORE INTO match_reports (match_id, role, player_id, outcome, pool, created) VALUES (?1, ?2, ?3, ?4, ?5, ?6)")
      .bind(id, role, me, outcome, pool, Date.now()).run();
  }
  return Response.json(await settle(env, id, await reportsOf(env, id)));
}

async function reportsOf(env: ResultsEnv, id: string): Promise<Report[]> {
  return (await env.DB.prepare("SELECT role, player_id, outcome, pool, created FROM match_reports WHERE match_id = ?1").bind(id).all<Report>()).results;
}

/** What the reports (and, for a lone report, the relay room's own record) say right now; null = nothing to settle yet. */
function decide(env: ResultsEnv, reports: Report[], room: RoomRow | null, now: number): { status: string; winner: string | null } | null {
  const host = reports.find(r => r.role === "host"), guest = reports.find(r => r.role === "guest");
  if (host && guest) {
    const agree = host.pool === guest.pool && host.player_id !== guest.player_id && opposite[host.outcome] === guest.outcome;
    if (!agree) return { status: "disputed", winner: null };
    return { status: "verified", winner: host.outcome === "win" ? "host" : host.outcome === "loss" ? "guest" : "draw" };
  }
  const one = host ?? guest;
  if (!one) return null;
  if (one.outcome === "loss") return { status: "conceded", winner: null };          // an admission against one's own interest
  if (one.outcome === "win" && room && room.left_role === otherRole(one.role) && room.left_at !== null && room.rejoined === null
      && room.left_at - room.started >= ms(env.RESULT_MIN_MS, MIN_MATCH_MS) && Date.now() - room.left_at >= ms(env.RESULT_GRACE_MS, LEAVE_GRACE_MS)) {
    return { status: "forfeit", winner: one.role };
  }
  if (now - one.created >= PENDING_TTL_MS) return { status: "void", winner: null };
  return null;
}

/** Settles the match when it can be (idempotent) and applies the verdict to the ratings and the mastery XP (also idempotent: a failed apply is retried here). */
async function settle(env: ResultsEnv, id: string, reports: Report[]): Promise<Verdict> {
  const v = await store(env, id, reports);
  if (v.status !== "pending") {
    await applyRank(env, id);
    await applyMastery(env, id);
  }
  return v;
}

/** verified and disputed are final; conceded, forfeit and void are revised when the other seat reports. */
async function store(env: ResultsEnv, id: string, reports: Report[]): Promise<Verdict> {
  const row = await env.DB.prepare("SELECT status, winner FROM matches WHERE match_id = ?1").bind(id).first<{ status: string; winner: string | null }>();
  const done = (r: { status: string; winner: string | null }): Verdict => (r.winner ? { status: r.status, winner: r.winner } : { status: r.status });
  if (row && (row.status === "verified" || row.status === "disputed")) return done(row);

  const room = await env.DB.prepare("SELECT started, left_role, left_at, rejoined FROM match_rooms WHERE match_id = ?1").bind(id).first<RoomRow>();
  const next = decide(env, reports, room, Date.now());
  if (!next) return row ? done(row) : { status: "pending" };
  const host = reports.find(r => r.role === "host"), guest = reports.find(r => r.role === "guest");
  const pool = (host ?? guest)?.pool ?? "";
  await env.DB.prepare(`INSERT INTO matches (match_id, pool, host_id, guest_id, winner, status, resolved) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)
      ON CONFLICT(match_id) DO UPDATE SET pool = ?2, host_id = ?3, guest_id = ?4, winner = ?5, status = ?6, resolved = ?7
      WHERE matches.status NOT IN ('verified', 'disputed')`)
    .bind(id, pool, host?.player_id ?? null, guest?.player_id ?? null, next.winner, next.status, Date.now()).run();
  const final = await env.DB.prepare("SELECT status, winner FROM matches WHERE match_id = ?1").bind(id).first<{ status: string; winner: string | null }>();
  return final ? done(final) : done(next);
}
