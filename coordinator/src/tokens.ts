/** Match tokens: HMAC(match.role) with TOKEN_SECRET opens a relay room as that role (see index.ts Room, challenge.ts, Queue). */
export type Role = "host" | "guest";

export async function matchToken(env: { TOKEN_SECRET?: string }, match: string, role: Role): Promise<string> {
  const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(env.TOKEN_SECRET ?? "dev-secret"),
    { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const mac = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(`${match}.${role}`));
  return [...new Uint8Array(mac)].map(b => b.toString(16).padStart(2, "0")).join("");
}
