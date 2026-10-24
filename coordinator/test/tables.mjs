// Draft tables (src/tables.ts). Needs `npm run dev` (TABLE_FIRST_MS 1500, TABLE_JOIN_MS 1000). Usage: node test/tables.mjs [ws://host:port]
const BASE = process.argv[2] ?? "ws://127.0.0.1:8787";
let failed = 0;
const check = (ok, what) => { console.log((ok ? "ok   " : "FAIL ") + what); if (!ok) failed++; };
const sleep = ms => new Promise(r => setTimeout(r, ms));
const run = Date.now().toString(36);                // fresh queues per run (a DO keeps its state across runs)

function open(path) {
  const ws = new WebSocket(BASE + path);
  ws.binaryType = "arraybuffer";
  const inbox = [];
  let waiter = null;
  ws.onmessage = e => { inbox.push(e.data); waiter?.(); };
  const closed = new Promise(r => ws.addEventListener("close", e => r(e.code)));
  const opened = new Promise((res, rej) => { ws.onopen = res; ws.onerror = () => rej(new Error("connect " + path)); });
  const next = async (ms = 3000) => {
    const end = Date.now() + ms;
    while (!inbox.length) {
      if (Date.now() > end) return undefined;
      await new Promise(r => { waiter = r; setTimeout(r, 100); });
    }
    return inbox.shift();
  };
  /** The next text message of type t (skipping others), parsed. */
  const nextOf = async (t, ms = 3000) => {
    const end = Date.now() + ms;
    for (;;) {
      const m = await next(Math.max(1, end - Date.now()));
      if (m === undefined) return undefined;
      if (typeof m === "string" && m !== "pong") { const o = JSON.parse(m); if (o.t === t) return o; }
    }
  };
  return { ws, opened, closed, next, nextOf };
}

const q = (key, id, name, fp = "db1") => open(`/tablequeue?q=PremierDraft_VCUBE&fp=${fp}&v=t1&id=${id}&name=${name}&code=${key}${run}`);
const room = (t, slot = t.slot, token = t.token) => open(`/table/${t.table}?slot=${slot}&token=${token}`);

// 1. forming: the deadline is FIRST after the first player, +JOIN for every later one
const a = q("A", "p1", "Alice"); await a.opened;
check((await a.nextOf("pod"))?.n === 1, "first player: pod 1 of 8");
await sleep(800);
const b = q("A", "p2", "Bob"); await b.opened;
check((await a.nextOf("pod"))?.n === 2 && (await b.nextOf("pod"))?.n === 2, "second player: both see pod 2");
const t0 = Date.now();
check((await a.nextOf("table", 700 + 900)) === undefined, "not formed at the first player's 1.5 s (extended by the join)");
const ta = await a.nextOf("table", 2000), tb = await b.nextOf("table", 2000);
check(ta?.role === "host" && ta.slot === 0 && tb?.role === "guest" && tb.slot === 1, "first player hosts (slot 0), second is slot 1");
check(ta.table === tb.table && ta.token !== tb.token && ta.n === 2 && tb.names.join() === "Alice,Bob", "same table, own tokens, names in slot order");
check(Date.now() - t0 < 2500 && await a.closed === 1000, "formed at the extended deadline, queue socket closed");

// 2. other card db / friend code / same player id stay apart; leaving lowers the count
const c = q("B", "p3", "Carol"); await c.opened; await c.nextOf("pod");
const d = q("B", "p4", "Dan", "db2"); await d.opened;
check((await d.nextOf("pod"))?.n === 1, "another card db is another queue");
const e = q("B", "p5", "Eve"); await e.opened;
check((await c.nextOf("pod"))?.n === 2, "same key: pod 2");
const e2 = q("B", "p5", "Eve2"); await e2.opened;
check((await c.nextOf("pod"))?.n === 2, "the same player id replaces its old ticket");   // (Node reports the server's close ~10 s late: not awaited)
e2.ws.close();
let pod; do pod = await c.nextOf("pod", 1500); while (pod && pod.n !== 1);
check(pod?.n === 1, "leaving lowers the count");
const alone = await c.nextOf("table", 4000);
check(alone?.role === "host" && alone.n === 1, "alone at the deadline: a table of one");
d.ws.close();

// 3. eight players form a table at once
const eight = [];
for (let i = 0; i < 8; i++) { const s = q("C", "f" + i, "P" + i); await s.opened; eight.push(s); }
const seats = await Promise.all(eight.map(s => s.nextOf("table", 1000)));
check(seats.every(s => s && s.n === 8) && seats.map(s => s.slot).join() === "0,1,2,3,4,5,6,7", "8 players: seated at once in arrival order");

// 4. the room
const T = seats[0].table;
const g1 = room(seats[1]); await g1.opened;
const g2 = room(seats[2]); await g2.opened;
check((await g1.nextOf("host", 400)) === undefined, "no host yet: a guest is not told to send its hello");
const h = room(seats[0]); await h.opened;
const ins = [await h.nextOf("in"), await h.nextOf("in")].map(x => x?.slot).sort().join();
check(ins === "1,2" && (await g1.nextOf("host"))?.t === "host" && (await g2.nextOf("host"))?.t === "host", "host told who is in; guests told the host is in");
g1.ws.send(new Uint8Array([10, 7, 42]));
const up = new Uint8Array(await h.next());
check(up.length === 3 && up[0] === 10 && up[1] === 1 && up[2] === 42, "guest -> host: the slot byte is the sender's");
h.ws.send(new Uint8Array([11, 2, 5]));
const down = new Uint8Array(await g2.next());
check(down[0] === 11 && down[2] === 5 && (await g1.next(400)) === undefined, "host -> one guest only");
const big = new Uint8Array(100_000).map((_, i) => i % 251); big[0] = 12; big[1] = 0xff;
h.ws.send(big);
const b1 = new Uint8Array(await g1.next()), b2 = new Uint8Array(await g2.next());
check(b1.length === big.length && b2.every((v, i) => v === big[i]), "host -> every guest (0xFF), 100 KB intact");
h.ws.send("ping");
check(await h.next() === "pong", "ping answered");

const dup = room(seats[1]);
check(await dup.opened.then(() => false, () => true), "a connected slot cannot be opened twice");
const bad = room(seats[3], 3, seats[4].token);
check(await bad.opened.then(() => false, () => true), "another slot's token is refused");

g1.ws.close();
check((await h.nextOf("out"))?.slot === 1, "host told a guest went away");
const g1b = room(seats[1]); await g1b.opened;
check((await h.nextOf("in"))?.slot === 1 && (await g1b.nextOf("host"))?.t === "host", "the guest reconnects to its slot");

h.ws.close();
check((await g1b.nextOf("left"))?.t === "left" && (await g2.nextOf("left"))?.t === "left", "host gone: every guest told");
check(await g2.closed === 1000, "guests closed");
const late = room(seats[3]);
check(await late.opened.then(() => false, () => true), "nobody joins a table that is over");
void T;

console.log(failed ? `${failed} FAILED` : "all passed");
process.exit(failed ? 1 : 0);
