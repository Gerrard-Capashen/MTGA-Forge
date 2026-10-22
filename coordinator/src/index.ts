/**
 * MTGA-Forge coordinator: matchmaking + WebSocket relay for player-vs-player matches.
 *
 *   GET  /health
 *   WS   /queue?q=CasualPlay_Modern&fp=<card db>&v=<peer protocol>&id=<player id>&name=<name>[&code=<friend code>]
 *        waits until a second player asks for the same (queue, card db, protocol, code); both get
 *        {"t":"paired","role":"host"|"guest","match":"<id>","token":"<hex>","opp":"<name>"} and the socket is closed.
 *   WS   /match/<id>?role=host|guest&token=<hex>
 *        relays every message to the other side. Binary messages = peer frames (type byte + body), text = control:
 *        {"t":"peer"} the other side is connected, {"t":"left"} the other side is gone (the relay closes both).
 *
 * Accounts (src/accounts.ts): /register, /auth/*, /me, /players/by-name. Friends (src/friends.ts, src/hub.ts): /friends*, /blocks*,
 *   WS /session?token=... (presence and pushes). The relay inspects and stores nothing: the host's engine
 * stays authoritative (casual play).
 */

import { authenticateToken, handleAccounts } from "./accounts";
import { handleFriends, hubOf, type FriendsEnv } from "./friends";

export { Hub } from "./hub";

export interface Env extends FriendsEnv {
  QUEUE: DurableObjectNamespace;
  ROOM: DurableObjectNamespace;
  TOKEN_SECRET?: string;
  /** "1" lets one player id pair with itself (local tests with two clients on one profile). */
  ALLOW_SELF_PAIRING?: string;
}

const NAME = /^[A-Za-z0-9_.\-]{1,64}$/;
const WAIT_MS = 5 * 60_000;         // a waiting ticket older than this is ignored
const ROOM_JOIN_MS = 60_000;        // both sides must be in the room by then
const ROOM_MAX_MS = 6 * 3_600_000;  // hard cap of one match

type Role = "host" | "guest";
const other = (r: Role): Role => (r === "host" ? "guest" : "host");

async function token(env: Env, match: string, role: Role): Promise<string> {
  const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(env.TOKEN_SECRET ?? "dev-secret"),
    { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const mac = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(`${match}.${role}`));
  return [...new Uint8Array(mac)].map(b => b.toString(16).padStart(2, "0")).join("");
}

function text(status: number, body: string): Response {
  return new Response(body, { status, headers: { "content-type": "text/plain; charset=utf-8" } });
}

export default {
  async fetch(req: Request, env: Env): Promise<Response> {
    const url = new URL(req.url);
    if (url.pathname === "/health") return Response.json({ ok: true, service: "mtga-forge-coordinator" });

    const account = await handleAccounts(req, env, url);
    if (account) return account;
    const friends = await handleFriends(req, env, url);
    if (friends) return friends;

    if (req.headers.get("Upgrade") !== "websocket") return text(426, "WebSocket expected");

    if (url.pathname === "/session") {
      const id = await authenticateToken(env, url.searchParams.get("token") ?? "");
      if (!id) return text(401, "sign in first");
      const headers = new Headers(req.headers);
      headers.set("x-player-id", id);                                   // set here only: a client cannot choose who it is
      return hubOf(env).fetch(new Request("https://hub/session?status=" + encodeURIComponent(url.searchParams.get("status") ?? "Available"), { headers }));
    }

    if (url.pathname === "/queue") {
      const q = url.searchParams.get("q") ?? "";
      const fp = url.searchParams.get("fp") ?? "";
      const v = url.searchParams.get("v") ?? "";
      const code = url.searchParams.get("code") ?? "";
      if (!NAME.test(q) || !NAME.test(v) || fp.length < 1 || fp.length > 128 || code.length > 32) return text(400, "bad queue request");
      return env.QUEUE.get(env.QUEUE.idFromName(`${q}|${fp}|${v}|${code}`)).fetch(req);
    }

    const m = url.pathname.match(/^\/match\/([0-9a-f-]{36})$/);
    if (m) {
      const role = url.searchParams.get("role");
      if (role !== "host" && role !== "guest") return text(400, "bad role");
      if (url.searchParams.get("token") !== await token(env, m[1], role)) return text(403, "bad token");
      return env.ROOM.get(env.ROOM.idFromName(m[1])).fetch(req);
    }
    return text(404, "not found");
  },
} satisfies ExportedHandler<Env>;

interface Ticket { id: string; name: string; at: number }

/** One instance per (queue, card database, protocol, friend code): the players in it can play each other. */
export class Queue implements DurableObject {
  constructor(private ctx: DurableObjectState, private env: Env) {
    // "ping" -> "pong" is answered without waking the object
    ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong"));
  }

  async fetch(req: Request): Promise<Response> {
    const url = new URL(req.url);
    const ticket: Ticket = {
      id: (url.searchParams.get("id") ?? "").slice(0, 64) || crypto.randomUUID(),
      name: (url.searchParams.get("name") ?? "Player").slice(0, 40),
      at: Date.now(),
    };
    const pair = new WebSocketPair();
    const [client, server] = [pair[0], pair[1]];
    this.ctx.acceptWebSocket(server);
    server.serializeAttachment(ticket);
    await this.tryPair(server, ticket);
    return new Response(null, { status: 101, webSocket: client });
  }

  private async tryPair(ws: WebSocket, mine: Ticket): Promise<void> {
    const now = Date.now();
    const waiting = this.ctx.getWebSockets()
      .filter(w => w !== ws && w.readyState === WebSocket.READY_STATE_OPEN)
      .map(w => ({ w, t: w.deserializeAttachment() as Ticket | null }))
      .filter((x): x is { w: WebSocket; t: Ticket } => x.t !== null && now - x.t.at < WAIT_MS)
      .filter(x => this.env.ALLOW_SELF_PAIRING === "1" || x.t.id !== mine.id)
      .sort((a, b) => a.t.at - b.t.at);
    const first = waiting[0];
    if (!first) return;

    const match = crypto.randomUUID();       // the player who waited longest hosts
    const send = async (w: WebSocket, role: Role, opp: string) => {
      w.send(JSON.stringify({ t: "paired", role, match, token: await token(this.env, match, role), opp }));
      w.close(1000, "paired");
    };
    await send(first.w, "host", mine.name);
    await send(ws, "guest", first.t.name);
  }

  webSocketMessage(): void { /* pings are auto-answered; nothing else is expected */ }
  webSocketClose(ws: WebSocket, code: number): void { try { ws.close(code === 1005 ? 1000 : code); } catch { /* already closed */ } }
}

/** One instance per match: forwards messages between the host and the guest. */
export class Room implements DurableObject {
  private pending: Record<Role, (string | ArrayBuffer)[]> = { host: [], guest: [] };

  constructor(private ctx: DurableObjectState, private env: Env) { }

  async fetch(req: Request): Promise<Response> {
    const role = new URL(req.url).searchParams.get("role") as Role;
    if (this.ctx.getWebSockets(role).length > 0) return text(409, `${role} is already connected`);
    if (this.ctx.getWebSockets().length === 0) await this.ctx.storage.setAlarm(Date.now() + ROOM_JOIN_MS);

    const pair = new WebSocketPair();
    const [client, server] = [pair[0], pair[1]];
    this.ctx.acceptWebSocket(server, [role]);

    const peer = this.ctx.getWebSockets(other(role))[0];
    if (peer) {
      await this.ctx.storage.setAlarm(Date.now() + ROOM_MAX_MS);
      server.send(JSON.stringify({ t: "peer" }));
      peer.send(JSON.stringify({ t: "peer" }));
      for (const m of this.pending[other(role)]) server.send(m);   // what the other side sent before we were here
      this.pending[other(role)] = [];
    }
    return new Response(null, { status: 101, webSocket: client });
  }

  webSocketMessage(ws: WebSocket, message: string | ArrayBuffer): void {
    const role = this.ctx.getTags(ws)[0] as Role;
    const peer = this.ctx.getWebSockets(other(role))[0];
    if (peer) { peer.send(message); return; }
    const q = this.pending[role];                                    // the guest talks before the host has connected
    const size = q.reduce((n, m) => n + (typeof m === "string" ? m.length : m.byteLength), 0);
    if (q.length < 64 && size < 1_000_000) q.push(message);
  }

  webSocketClose(ws: WebSocket, code: number): void {
    const role = this.ctx.getTags(ws)[0] as Role;
    const peer = this.ctx.getWebSockets(other(role))[0];
    if (peer) {
      try { peer.send(JSON.stringify({ t: "left" })); peer.close(1000, "peer left"); } catch { /* gone */ }
    }
    try { ws.close(code === 1005 ? 1000 : code); } catch { /* already closed */ }
  }

  webSocketError(ws: WebSocket): void { this.webSocketClose(ws, 1011); }

  /** The join deadline (one side never came) or the match cap, whichever alarm is set: either way the room ends. */
  async alarm(): Promise<void> {
    for (const w of this.ctx.getWebSockets()) { try { w.close(1000, "room closed"); } catch { /* gone */ } }
  }
}
