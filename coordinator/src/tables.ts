/**
 * Premier draft tables: a queue that forms a table of up to 8 people, then a relay room for that table. One player's
 * server (the host, slot 0) runs the draft; the room only routes frames, it inspects and stores nothing of the draft.
 *
 *   WS /tablequeue?q=PremierDraft_VCUBE&fp=<card db>&v=<table protocol>&id=<player id>&name=<name>[&code=<friend code>]
 *        {"t":"pod","n":<people waiting>,"cap":8} whenever the count changes. The table forms when 8 are in, or at the
 *        deadline: FIRST_MS after the first player, plus JOIN_MS for every player who joins after them. Then each gets
 *        {"t":"table","role":"host"|"guest","table":"<id>","slot":<0..7>,"token":"<hex>","n":<people>,"names":[...]}
 *        and the socket is closed. Slot 0 (who waited longest) hosts.
 *   WS /table/<id>?slot=<n>&token=<hex>
 *        binary = table frames [type][slot][body]. From a guest the room sets the slot byte to the sender's slot and
 *        forwards to the host; from the host the slot byte is the target, 0xFF = every guest.
 *        Text control: to the host {"t":"in","slot"} / {"t":"out","slot"} (a guest connected / went away); to a guest
 *        {"t":"host"} (the host is in: send your hello now) and {"t":"left"} (the host is gone: the table is over).
 *        A guest may reconnect to its slot with the same token while the table lasts. "ping" -> "pong" is auto-answered.
 */

import { slotToken } from "./tokens";

export interface TablesEnv {
  TABLE_QUEUE: DurableObjectNamespace;
  TABLE_ROOM: DurableObjectNamespace;
  TOKEN_SECRET?: string;
  ALLOW_SELF_PAIRING?: string;
  /** Test overrides (milliseconds), see package.json "dev". */
  TABLE_FIRST_MS?: string;
  TABLE_JOIN_MS?: string;
}

const NAME = /^[A-Za-z0-9_.\-]{1,64}$/;
const CAP = 8;
const FIRST_MS = 60_000;
const JOIN_MS = 30_000;
const HOST_JOIN_MS = 60_000;          // the host must be in the room by then
const TABLE_MAX_MS = 3 * 3_600_000;   // hard cap of one table
const BROADCAST = 0xff;

const ms = (v: string | undefined, d: number) => { const n = Number(v); return Number.isFinite(n) && n > 0 ? n : d; };

function text(status: number, body: string): Response {
  return new Response(body, { status, headers: { "content-type": "text/plain; charset=utf-8" } });
}

/** The /tablequeue and /table/<id> routes, null for other paths. */
export async function handleTables(req: Request, env: TablesEnv, url: URL): Promise<Response | null> {
  if (url.pathname === "/tablequeue") {
    if (req.headers.get("Upgrade") !== "websocket") return text(426, "WebSocket expected");
    const q = url.searchParams.get("q") ?? "";
    const fp = url.searchParams.get("fp") ?? "";
    const v = url.searchParams.get("v") ?? "";
    const code = url.searchParams.get("code") ?? "";
    if (!NAME.test(q) || !NAME.test(v) || fp.length < 1 || fp.length > 128 || code.length > 32) return text(400, "bad queue request");
    return env.TABLE_QUEUE.get(env.TABLE_QUEUE.idFromName(`${q}|${fp}|${v}|${code}`)).fetch(req);
  }
  const m = url.pathname.match(/^\/table\/([0-9a-f-]{36})$/);
  if (!m) return null;
  if (req.headers.get("Upgrade") !== "websocket") return text(426, "WebSocket expected");
  const slot = Number(url.searchParams.get("slot"));
  if (!Number.isInteger(slot) || slot < 0 || slot >= CAP) return text(400, "bad slot");
  if (url.searchParams.get("token") !== await slotToken(env, m[1], slot)) return text(403, "bad token");
  return env.TABLE_ROOM.get(env.TABLE_ROOM.idFromName(m[1])).fetch(req);
}

interface Ticket { id: string; name: string; at: number }

/** One instance per (event, card database, protocol, friend code): forms one table at a time. */
export class TableQueue implements DurableObject {
  constructor(private ctx: DurableObjectState, private env: TablesEnv) {
    ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong"));
  }

  async fetch(req: Request): Promise<Response> {
    const url = new URL(req.url);
    const ticket: Ticket = {
      id: (url.searchParams.get("id") ?? "").slice(0, 64) || crypto.randomUUID(),
      name: (url.searchParams.get("name") ?? "Player").slice(0, 40),
      at: Date.now(),
    };
    if (this.env.ALLOW_SELF_PAIRING !== "1") {
      for (const x of this.waiting()) if (x.t.id === ticket.id) { try { x.w.close(1000, "replaced"); } catch { /* gone */ } }
    }
    const pair = new WebSocketPair();
    const [client, server] = [pair[0], pair[1]];
    this.ctx.acceptWebSocket(server);
    server.serializeAttachment(ticket);

    const now = Date.now();
    let deadline = await this.ctx.storage.get<number>("deadline");
    deadline = deadline && this.waiting().length > 1 ? deadline + ms(this.env.TABLE_JOIN_MS, JOIN_MS) : now + ms(this.env.TABLE_FIRST_MS, FIRST_MS);
    await this.ctx.storage.put("deadline", deadline);
    await this.ctx.storage.setAlarm(deadline);
    if (this.waiting().length >= CAP) await this.form();
    else this.announce();
    return new Response(null, { status: 101, webSocket: client });
  }

  /** Open tickets, oldest first; {@code gone} is a socket being closed (it may still read as open in its close handler). */
  private waiting(gone?: WebSocket): { w: WebSocket; t: Ticket }[] {
    return this.ctx.getWebSockets()
      .filter(w => w !== gone && w.readyState === WebSocket.READY_STATE_OPEN)
      .map(w => ({ w, t: w.deserializeAttachment() as Ticket | null }))
      .filter((x): x is { w: WebSocket; t: Ticket } => x.t !== null)
      .sort((a, b) => a.t.at - b.t.at);
  }

  private announce(gone?: WebSocket): void {
    const all = this.waiting(gone);
    const msg = JSON.stringify({ t: "pod", n: all.length, cap: CAP });
    for (const x of all) { try { x.w.send(msg); } catch { /* gone */ } }
  }

  /** The table is complete (or the deadline passed): hand out the slots, oldest ticket first (the host). */
  private async form(): Promise<void> {
    const seated = this.waiting().slice(0, CAP);
    await this.ctx.storage.delete("deadline");
    await this.ctx.storage.deleteAlarm();
    if (seated.length === 0) return;
    const table = crypto.randomUUID();
    const names = seated.map(x => x.t.name);
    for (let slot = 0; slot < seated.length; slot++) {
      const w = seated[slot].w;
      w.send(JSON.stringify({ t: "table", role: slot === 0 ? "host" : "guest", table, slot, token: await slotToken(this.env, table, slot), n: seated.length, names }));
      w.close(1000, "seated");
    }
    if (this.waiting().length > 0) {                      // more than 8 arrived at once: the rest start a new table
      const d = Date.now() + ms(this.env.TABLE_FIRST_MS, FIRST_MS);
      await this.ctx.storage.put("deadline", d);
      await this.ctx.storage.setAlarm(d);
      this.announce();
    }
  }

  async alarm(): Promise<void> { await this.form(); }

  webSocketMessage(): void { /* pings are auto-answered; nothing else is expected */ }

  async webSocketClose(ws: WebSocket, code: number): Promise<void> {
    try { ws.close(code === 1005 ? 1000 : code); } catch { /* already closed */ }    if (this.waiting(ws).length === 0) {
      await this.ctx.storage.delete("deadline");
      await this.ctx.storage.deleteAlarm();
    } else {
      this.announce(ws);                                   // leaving lowers the count; the deadline stays
    }
  }

  webSocketError(ws: WebSocket): Promise<void> { return this.webSocketClose(ws, 1011); }
}

/** One instance per table: routes frames between the host (slot 0) and the guests. */
export class TableRoom implements DurableObject {
  constructor(private ctx: DurableObjectState, private env: TablesEnv) {
    ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong"));
  }

  async fetch(req: Request): Promise<Response> {
    const slot = Number(new URL(req.url).searchParams.get("slot"));
    if (await this.ctx.storage.get<boolean>("over")) return text(410, "the table is over");
    if (this.ctx.getWebSockets(tag(slot)).length > 0) return text(409, `slot ${slot} is already connected`);
    if (!await this.ctx.storage.get<number>("opened")) {
      await this.ctx.storage.put("opened", Date.now());
      await this.ctx.storage.setAlarm(Date.now() + HOST_JOIN_MS);
    }

    const pair = new WebSocketPair();
    const [client, server] = [pair[0], pair[1]];
    this.ctx.acceptWebSocket(server, [tag(slot)]);

    const host = this.host();
    if (slot === 0) {
      await this.ctx.storage.setAlarm((await this.ctx.storage.get<number>("opened"))! + TABLE_MAX_MS);
      for (const g of this.guests()) {
        server.send(JSON.stringify({ t: "in", slot: slotOf(this.ctx.getTags(g)) }));
        g.send(JSON.stringify({ t: "host" }));
      }
    } else if (host) {
      host.send(JSON.stringify({ t: "in", slot }));
      server.send(JSON.stringify({ t: "host" }));
    }
    return new Response(null, { status: 101, webSocket: client });
  }

  private host(): WebSocket | undefined { return this.ctx.getWebSockets(tag(0))[0]; }

  private guests(): WebSocket[] { return this.ctx.getWebSockets().filter(w => slotOf(this.ctx.getTags(w)) !== 0); }

  webSocketMessage(ws: WebSocket, message: string | ArrayBuffer): void {
    if (typeof message === "string") return;               // only "ping" is expected (auto-answered)
    const bytes = new Uint8Array(message);
    if (bytes.length < 2) return;
    const from = slotOf(this.ctx.getTags(ws));
    if (from !== 0) {
      bytes[1] = from;                                     // a guest cannot speak for another seat
      this.host()?.send(bytes);
      return;
    }
    const to = bytes[1];
    for (const g of to === BROADCAST ? this.guests() : this.ctx.getWebSockets(tag(to))) {
      try { g.send(bytes); } catch { /* gone */ }
    }
  }

  async webSocketClose(ws: WebSocket, code: number): Promise<void> {
    const slot = slotOf(this.ctx.getTags(ws));
    try { ws.close(code === 1005 ? 1000 : code); } catch { /* already closed */ }
    if (slot === 0) {
      await this.end("host left");
      return;
    }
    this.host()?.send(JSON.stringify({ t: "out", slot }));
  }

  webSocketError(ws: WebSocket): Promise<void> { return this.webSocketClose(ws, 1011); }

  /** The host never came, or the table hit its cap. */
  async alarm(): Promise<void> { await this.end("table closed"); }

  private async end(why: string): Promise<void> {
    await this.ctx.storage.put("over", true);
    await this.ctx.storage.deleteAlarm();
    for (const g of this.ctx.getWebSockets()) {
      try { g.send(JSON.stringify({ t: "left" })); g.close(1000, why); } catch { /* gone */ }
    }
  }
}

const tag = (slot: number) => "s" + slot;
const slotOf = (tags: string[]) => Number((tags[0] ?? "s-1").slice(1));
