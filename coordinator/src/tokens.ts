/** Match tokens: HMAC(match.role) with TOKEN_SECRET opens a relay room as that role (see index.ts Room, challenge.ts, Queue). */
export type Role = "host" | "guest";

export async function matchToken(env: { TOKEN_SECRET?: string }, match: string, role: Role): Promise<string> {
  return hmac(env, `${match}.${role}`);
}

/** Draft table seats (tables.ts): HMAC(table.s<slot>) opens the table's room as that slot. */
export async function slotToken(env: { TOKEN_SECRET?: string }, table: string, slot: number): Promise<string> {
  return hmac(env, `${table}.s${slot}`);
}

async function hmac(env: { TOKEN_SECRET?: string }, data: string): Promise<string> {
  const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(env.TOKEN_SECRET ?? "dev-secret"),
    { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const mac = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(data));
  return [...new Uint8Array(mac)].map(b => b.toString(16).padStart(2, "0")).join("");
}
