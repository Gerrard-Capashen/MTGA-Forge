// Needs `npm run dev` (wrangler dev on :8787). Usage: node test/smoke.mjs [ws://host:port]
const BASE = process.argv[2] ?? "ws://127.0.0.1:8787";
let failed = 0;
const check = (ok, what) => { console.log((ok ? "ok   " : "FAIL ") + what); if (!ok) failed++; };
const sleep = ms => new Promise(r => setTimeout(r, ms));

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
  return { ws, opened, closed, next };
}

const q = (id, name, extra = "") => open(`/queue?q=CasualPlay_Modern&fp=db1&v=2&id=${id}&name=${name}${extra}`);

// 1. pairing: the player who waited longest hosts
const a = q("p1", "Alice"); await a.opened;
await sleep(200);
const b = q("p2", "Bob"); await b.opened;
const pa = JSON.parse(await a.next()), pb = JSON.parse(await b.next());
check(pa?.t === "paired" && pa.role === "host" && pa.opp === "Bob", "first in the queue is host, sees Bob");
check(pb?.t === "paired" && pb.role === "guest" && pb.opp === "Alice", "second is guest, sees Alice");
check(pa.match === pb.match && pa.token !== pb.token, "same match id, different tokens");
check(await a.closed === 1000, "queue socket closed after pairing");

// 2. no pairing across queue / card db / friend code / same player
const c = open("/queue?q=CasualPlay_Standard&fp=db1&v=2&id=p3&name=C"); await c.opened;
const d = open("/queue?q=CasualPlay_Modern&fp=db2&v=2&id=p4&name=D"); await d.opened;
const e = q("p5", "E", "&code=ABCD"); await e.opened;
const f = q("p6", "F"); await f.opened;
const g = q("p6", "F2"); await g.opened;                      // same player id as f
await sleep(500);
check((await c.next(300)) === undefined && (await d.next(300)) === undefined && (await e.next(300)) === undefined, "other queue / card db / code do not pair");
check((await f.next(300)) === undefined && (await g.next(300)) === undefined, "a player id does not pair with itself");
const h = q("p7", "H", "&code=ABCD"); await h.opened;
check(JSON.parse(await e.next()).role === "host" && JSON.parse(await h.next()).role === "guest", "friend code pairs");
for (const x of [c, d, f, g]) x.ws.close();

// 3. relay: the guest talks before the host is in the room (buffered), then both directions, binary intact
const guest = open(`/match/${pa.match}?role=guest&token=${pb.token}`); await guest.opened;
guest.ws.send(new Uint8Array([1, 9, 9]));
await sleep(300);
const host = open(`/match/${pa.match}?role=host&token=${pa.token}`); await host.opened;
check(JSON.parse(await host.next()).t === "peer" && JSON.parse(await guest.next()).t === "peer", "both told the peer is here");
const early = new Uint8Array(await host.next());
check(early.length === 3 && early[0] === 1, "frame sent before the host connected is delivered");
const big = new Uint8Array(200_000).map((_, i) => i % 251);
host.ws.send(big);
const got = new Uint8Array(await guest.next());
check(got.length === big.length && got.every((v, i) => v === big[i]), "200 KB binary host -> guest intact");
guest.ws.send("hello");
check(await host.next() === "hello", "text guest -> host");

// 4. rejections
const dup = open(`/match/${pa.match}?role=host&token=${pa.token}`);
check(await dup.opened.then(() => false, () => true), "second host refused");
const bad = open(`/match/${pa.match}?role=guest&token=${pa.token}`);
check(await bad.opened.then(() => false, () => true), "wrong token refused");

// 5. leaving: the other side is told and closed
guest.ws.close();
const left = await host.next();
check(left && JSON.parse(left).t === "left", "host told the guest left");
check(await host.closed === 1000, "host socket closed after the guest left");

console.log(failed ? `${failed} FAILED` : "all passed");
process.exit(failed ? 1 : 0);
