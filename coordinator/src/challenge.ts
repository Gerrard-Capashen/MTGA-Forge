/**
 * A direct challenge between friends: the state of one "private game" the client's Challenge screen shows (players, settings, who is ready),
 * kept by one Durable Object per challenge id. The Worker (challenges.ts) authenticates and forwards {op, player, args}; every change
 * is pushed to the players' sessions through the Hub as {"t":"challenge","kind":...}:
 *   status    {challenge}                  the full state changed
 *   countdown {id, seconds}                the owner launched: the clients count down, then each one "issues"
 *   match     {id, match, role, token, opponent}   everybody issued: open the relay room /match/<match> as that role (the owner hosts)
 *   kick      {id}   close {id}
 * The invite itself travels between the two clients as an opaque friend message (Hub {"t":"game"}); joining needs the challenge id and a
 * friendship with the owner (checked in challenges.ts).
 */
import { matchToken, type Role } from "./tokens";
import type { FriendsEnv } from "./friends";

export interface ChallengeEnv extends FriendsEnv { TOKEN_SECRET?: string }

interface Cosmetics { avatar: string; sleeve: string; pet: string; title: string; emotes: string[] }
export interface Member {
  playerId: string; fullName: string; ready: boolean; teamId: number;
  deckId: string; deckName: string; artId: number; tileId: number; cosmetics: Cosmetics;
}
export interface State {
  id: string; title: string; name: string; playFirst: string; winCondition: string; limit: number; owner: string;
  status: "open" | "starting"; players: Member[]; issued: string[]; createdAt: number; launchedAt: number;
}

interface Op { op: string; player: { id: string; fullName: string }; args: Record<string, unknown> }

const NAME = /^DirectGame[A-Za-z]{0,30}$/;
const PLAY_FIRST = ["Random", "Challenger", "Opponent"];
const WIN = ["SingleElimination", "BestOf3"];
const IDLE_MS = 3 * 3_600_000;
const LAUNCH_MS = 90_000;            // everybody has this long to issue after the launch, else the challenge goes back to open
const COUNTDOWN_SECONDS = 10;           // the client waits a fixed 7 s and then countdown - 7: anything under 8 breaks its launch routine

const str = (v: unknown, max: number) => (typeof v === "string" ? v.slice(0, max) : "");
const num = (v: unknown) => (typeof v === "number" && Number.isFinite(v) ? Math.trunc(v) : 0);

function cosmetics(v: unknown): Cosmetics {
  const o = (v && typeof v === "object" ? v : {}) as Record<string, unknown>;
  return {
    avatar: str(o.avatar, 80), sleeve: str(o.sleeve, 80), pet: str(o.pet, 80), title: str(o.title, 80),
    emotes: Array.isArray(o.emotes) ? o.emotes.slice(0, 12).map(e => str(e, 80)) : [],
  };
}

export class Challenge implements DurableObject {
  constructor(private ctx: DurableObjectState, private env: ChallengeEnv) { }

  async fetch(req: Request): Promise<Response> {
    const { op, player, args } = await req.json() as Op;
    let s = await this.ctx.storage.get<State>("s") ?? null;

    if (op === "create") {
      if (s) return this.err(409, "exists");
      const name = str(args.name, 40) || "DirectGame";
      if (!NAME.test(name)) return this.err(400, "bad challenge name");
      s = {
        id: str(args.id, 40), title: str(args.title, 64), name, playFirst: "Random", winCondition: "SingleElimination", limit: 2, owner: player.id,
        status: "open", players: [this.member(player, args)], issued: [], createdAt: Date.now(), launchedAt: 0,
      };
      await this.save(s);
      await this.env.DB.prepare("INSERT OR IGNORE INTO challenge_players (challenge_id, player_id, created) VALUES (?1, ?2, ?3)").bind(s.id, player.id, Date.now()).run();
      return this.ok(s);
    }
    if (!s) return this.err(404, "no such challenge");

    if (op === "peek") return Response.json({ owner: s.owner, players: s.players.length });       // for the Worker's friendship check before a join
    if (op === "get") return s.players.some(p => p.playerId === player.id) ? this.ok(s) : this.err(403, "not in this challenge");

    if (op === "join") {
      if (s.players.some(p => p.playerId === player.id)) return this.ok(s);
      if (s.players.length >= s.limit) return this.err(409, "the challenge is full");
      if (s.status !== "open") return this.err(409, "the match is starting");
      s.players.push(this.member(player, args));
      s.players.forEach(p => { p.ready = false; });
      await this.save(s);
      await this.env.DB.prepare("INSERT OR IGNORE INTO challenge_players (challenge_id, player_id, created) VALUES (?1, ?2, ?3)").bind(s.id, player.id, Date.now()).run();
      await this.broadcast(s, { kind: "status", challenge: s });
      return this.ok(s);
    }

    const me = s.players.find(p => p.playerId === player.id);
    if (!me) return this.err(403, "not in this challenge");
    const owner = s.owner === player.id;

    switch (op) {
      case "settings": {
        if (!owner) return this.err(403, "only the owner changes the settings");
        if (s.status !== "open") return this.err(409, "the match is starting");
        const name = args.name === undefined ? s.name : str(args.name, 40);
        if (!NAME.test(name)) return this.err(400, "bad challenge name");
        s.name = name;
        if (typeof args.playFirst === "string" && PLAY_FIRST.includes(args.playFirst)) s.playFirst = args.playFirst;
        if (typeof args.winCondition === "string" && WIN.includes(args.winCondition)) s.winCondition = args.winCondition;
        const limit = num(args.limit);
        if (limit >= 2 && limit <= 2) s.limit = limit;                   // two players only: the engine plays 1 vs 1
        s.players.forEach(p => { p.ready = false; });                    // another format or rule: everybody checks again
        await this.save(s);
        await this.broadcast(s, { kind: "status", challenge: s });
        return this.ok(s);
      }
      case "player":
        me.teamId = num(args.teamId);
        await this.save(s);
        await this.broadcast(s, { kind: "status", challenge: s });
        return this.ok(s);
      case "ready":
        if (s.status !== "open") return this.err(409, "the match is starting");
        me.ready = true;
        me.deckId = str(args.deckId, 64); me.deckName = str(args.deckName, 64); me.artId = num(args.artId); me.tileId = num(args.tileId);
        me.cosmetics = cosmetics(args.cosmetics);
        await this.save(s);
        await this.broadcast(s, { kind: "status", challenge: s });
        return this.ok(s);
      case "unready":
        if (s.status !== "open") return this.err(409, "the match is starting");
        me.ready = false;
        await this.save(s);
        await this.broadcast(s, { kind: "status", challenge: s });
        return this.ok(s);
      case "launch":
        if (!owner) return this.err(403, "only the owner starts the match");
        if (s.status !== "open") return this.err(409, "already starting");
        if (s.players.length < 2) return this.err(409, "waiting for an opponent");
        if (!s.players.every(p => p.ready)) return this.err(409, "everybody has to be ready");
        s.status = "starting"; s.issued = []; s.launchedAt = Date.now();
        await this.save(s);
        await this.broadcast(s, { kind: "status", challenge: s });
        await this.broadcast(s, { kind: "countdown", id: s.id, seconds: COUNTDOWN_SECONDS });
        return this.ok(s);
      case "issue": {
        if (s.status !== "starting") return this.err(409, "the match was not started");
        if (!s.issued.includes(player.id)) s.issued.push(player.id);
        if (s.issued.length < s.players.length) { await this.save(s); return this.ok(s); }
        const match = crypto.randomUUID();
        const host = s.players.find(p => p.playerId === s.owner)!;
        for (const p of s.players) {
          const role: Role = p.playerId === s.owner ? "host" : "guest";
          const opp = p.playerId === s.owner ? s.players.find(x => x.playerId !== s.owner)! : host;
          await this.notify([p.playerId], { kind: "match", id: s.id, match, role, token: await matchToken(this.env, match, role), opponent: { playerId: opp.playerId, fullName: opp.fullName } });
        }
        s.status = "open"; s.issued = []; s.players.forEach(p => { p.ready = false; });      // back to the lobby for the next game
        await this.save(s);
        await this.broadcast(s, { kind: "status", challenge: s });
        return this.ok(s);
      }
      case "kick": {
        if (!owner) return this.err(403, "only the owner removes players");
        const target = str(args.playerId, 64);
        if (target === s.owner || !s.players.some(p => p.playerId === target)) return this.err(404, "no such player");
        await this.notify([target], { kind: "kick", id: s.id });
        return this.leave(s, target);
      }
      case "exit":
        return owner ? this.close(s, true) : this.leave(s, player.id);
      case "close":
        if (!owner) return this.err(403, "only the owner closes the challenge");
        return this.close(s, true);
      default:
        return this.err(400, "unknown op");
    }
  }

  async alarm(): Promise<void> {
    const s = await this.ctx.storage.get<State>("s");
    if (!s) return;
    if (s.status === "starting" && Date.now() - s.launchedAt >= LAUNCH_MS) {          // somebody never issued: back to the lobby
      s.status = "open"; s.issued = []; s.players.forEach(p => { p.ready = false; });
      await this.save(s);
      await this.broadcast(s, { kind: "status", challenge: s });
      return;
    }
    if (Date.now() - (await this.ctx.storage.get<number>("touched") ?? 0) >= IDLE_MS) await this.close(s, true);
  }

  private member(player: Op["player"], args: Record<string, unknown>): Member {
    return { playerId: player.id, fullName: player.fullName, ready: false, teamId: 0, deckId: "", deckName: "", artId: 0, tileId: 0, cosmetics: cosmetics(args.cosmetics) };
  }

  private async save(s: State): Promise<void> {
    await this.ctx.storage.put("s", s);
    await this.ctx.storage.put("touched", Date.now());
    await this.ctx.storage.setAlarm(Date.now() + (s.status === "starting" ? LAUNCH_MS : IDLE_MS));
  }

  private async leave(s: State, playerId: string): Promise<Response> {
    s.players = s.players.filter(p => p.playerId !== playerId);
    s.players.forEach(p => { p.ready = false; });
    if (s.status === "starting") { s.status = "open"; s.issued = []; }
    await this.save(s);
    await this.env.DB.prepare("DELETE FROM challenge_players WHERE challenge_id = ?1 AND player_id = ?2").bind(s.id, playerId).run();
    await this.broadcast(s, { kind: "status", challenge: s });
    return this.ok(s);
  }

  private async close(s: State, tellEveryone: boolean): Promise<Response> {
    if (tellEveryone) await this.broadcast(s, { kind: "close", id: s.id });
    await this.env.DB.prepare("DELETE FROM challenge_players WHERE challenge_id = ?1").bind(s.id).run();
    await this.ctx.storage.deleteAll();
    return this.ok(null);
  }

  private broadcast(s: State, event: Record<string, unknown>): Promise<void> { return this.notify(s.players.map(p => p.playerId), event); }

  private async notify(to: string[], event: Record<string, unknown>): Promise<void> {
    await this.env.HUB.get(this.env.HUB.idFromName("hub")).fetch("https://hub/notify", { method: "POST", body: JSON.stringify({ to, event: { t: "challenge", ...event } }) });
  }

  private ok(challenge: State | null): Response { return Response.json({ challenge }); }
  private err(status: number, error: string): Response { return Response.json({ error }, { status }); }
}
