/**
 * The Hub: one Durable Object holding every signed-in player's session WebSocket (hibernating, so idle sockets cost nothing).
 * It knows who is online and with which status, tells friends when that changes, and delivers the {"t":"changed"} pushes of
 * friends.ts. Friend lists live in D1; the Hub only reads them to know whom to tell.
 *
 *   /session   (from the Worker, after the token check, header x-player-id)  WebSocket. Server -> client:
 *                {"t":"hello","status":"Available"}   {"t":"presence","playerId":"...","status":"Busy"}   {"t":"changed"}
 *              client -> server: {"t":"status","status":"Available|Busy|Away|Offline"}  and "ping" (answered "pong" without waking the Hub)
 *   /notify    POST {to:[playerId], event}  send the event to those players' sessions
 *   /presence  POST {ids:[playerId]} -> {playerId: status}   ("Offline" when there is no session)
 *
 * A player has one session: a new one closes the older (code 4000). Status "Offline" keeps the socket but shows as offline.
 */
import type { FriendsEnv } from "./friends";

const STATUSES = ["Available", "Busy", "Away", "Offline"];

interface Session { playerId: string; status: string }

export class Hub implements DurableObject {
  constructor(private ctx: DurableObjectState, private env: FriendsEnv) {
    ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong"));
  }

  async fetch(req: Request): Promise<Response> {
    const url = new URL(req.url);

    if (url.pathname === "/notify" && req.method === "POST") {
      const { to, event } = await req.json() as { to: string[]; event: unknown };
      const text = JSON.stringify(event);
      for (const id of new Set(to)) for (const w of this.live(id)) w.send(text);
      return new Response("ok");
    }

    if (url.pathname === "/presence" && req.method === "POST") {
      const { ids } = await req.json() as { ids: string[] };
      return Response.json(Object.fromEntries(ids.map(id => [id, this.statusOf(id)])));
    }

    if (url.pathname === "/session") {
      const playerId = req.headers.get("x-player-id") ?? "";
      if (!playerId) return new Response("no player", { status: 400 });
      const wanted = url.searchParams.get("status") ?? "Available";
      const status = STATUSES.includes(wanted) ? wanted : "Available";
      for (const old of this.live(playerId)) { try { old.close(4000, "signed in elsewhere"); } catch { /* gone */ } }
      const pair = new WebSocketPair();
      const [client, server] = [pair[0], pair[1]];
      this.ctx.acceptWebSocket(server, [playerId]);
      server.serializeAttachment({ playerId, status } satisfies Session);
      server.send(JSON.stringify({ t: "hello", status }));
      await this.announce(playerId);
      return new Response(null, { status: 101, webSocket: client });
    }
    return new Response("not found", { status: 404 });
  }

  async webSocketMessage(ws: WebSocket, message: string | ArrayBuffer): Promise<void> {
    if (typeof message !== "string") return;
    let m: { t?: string; status?: string };
    try { m = JSON.parse(message); } catch { return; }
    const s = ws.deserializeAttachment() as Session | null;
    if (!s) return;
    if (m.t === "status" && typeof m.status === "string" && STATUSES.includes(m.status) && m.status !== s.status) {
      ws.serializeAttachment({ ...s, status: m.status } satisfies Session);
      await this.announce(s.playerId);
    }
  }

  async webSocketClose(ws: WebSocket, code: number): Promise<void> {
    const s = ws.deserializeAttachment() as Session | null;
    try { ws.close(code === 1005 ? 1000 : code); } catch { /* already closed */ }
    if (s && this.live(s.playerId).filter(w => w !== ws).length === 0) await this.announce(s.playerId, "Offline");
  }

  async webSocketError(ws: WebSocket): Promise<void> { await this.webSocketClose(ws, 1011); }

  private live(playerId: string): WebSocket[] {
    return this.ctx.getWebSockets(playerId).filter(w => w.readyState === WebSocket.READY_STATE_OPEN);
  }

  private statusOf(playerId: string): string {
    const w = this.live(playerId)[0];
    return (w?.deserializeAttachment() as Session | null)?.status ?? "Offline";
  }

  /** Tells the player's online friends the player's status (the player's own sockets are not told: they know). */
  private async announce(playerId: string, status = this.statusOf(playerId)): Promise<void> {
    const friends = (await this.env.DB.prepare(
      "SELECT CASE WHEN a = ?1 THEN b ELSE a END AS id FROM friendships WHERE (a = ?1 OR b = ?1) AND status = 'accepted'").bind(playerId).all<{ id: string }>()).results;
    const text = JSON.stringify({ t: "presence", playerId, status });
    for (const f of friends) for (const w of this.live(f.id)) w.send(text);
  }
}
