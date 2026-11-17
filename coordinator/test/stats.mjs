// Needs `npm run dev` (wrangler dev on :8787, STATS_CACHE_S=0). Usage: node test/stats.mjs [http://host:port]
const HTTP = process.argv[2] ?? "http://127.0.0.1:8787";
const WS = HTTP.replace(/^http/, "ws");
let failed = 0;
const check = (ok, what) => { console.log((ok ? "ok   " : "FAIL ") + what); if (!ok) failed++; };
const sleep = ms => new Promise(r => setTimeout(r, ms));
const stats = async () => { const r = await fetch(HTTP + "/stats"); return { r, s: await r.json() }; };
const open = path => { const ws = new WebSocket(WS + path); return { ws, opened: new Promise((res, rej) => { ws.onopen = res; ws.onerror = () => rej(new Error(path)); }) }; };
const waitingIn = (s, kind, queue) => s.waiting.find(w => w.kind === kind && w.queue === queue)?.n ?? 0;

// 1. shape, CORS, no names or ids anywhere
const { r, s } = await stats();
check(r.status === 200 && r.headers.get("access-control-allow-origin") === "*", "GET /stats is public, CORS open");
check(typeof s.players?.total === "number" && typeof s.online?.total === "number" && Array.isArray(s.waiting), "players, online and waiting counts");
check(s.matches?.perDay?.length === 30 && s.rank?.classes?.length === 6, "30 days of matches, 6 rank classes");
check(!/player_id|"name"|"tag"|pubkey/.test(JSON.stringify(s)), "no player fields in the answer");

// 2. a public queue is counted, a friend-code queue is not; leaving lowers the count
const q = `StatsTest_${Date.now()}`;
const a = open(`/queue?q=${q}&fp=db1&v=2&id=s1&name=A`); await a.opened;
const p = open(`/queue?q=${q}P&fp=db1&v=2&id=s2&name=B&code=SECRET`); await p.opened;
const t = open(`/tablequeue?q=${q}T&fp=db1&v=2&id=s3&name=C`); await t.opened;
await sleep(400);
let now = (await stats()).s;
check(waitingIn(now, "match", q) === 1, "public match queue: 1 waiting");
check(waitingIn(now, "match", q + "P") === 0, "friend-code queue not reported");
check(waitingIn(now, "table", q + "T") === 1, "public draft table queue: 1 waiting");
a.ws.close(); p.ws.close(); t.ws.close();
await sleep(600);
now = (await stats()).s;
check(waitingIn(now, "match", q) === 0 && waitingIn(now, "table", q + "T") === 0, "leaving the queue clears the count");

console.log(failed ? `${failed} FAILED` : "all ok");
process.exit(failed ? 1 : 0);
