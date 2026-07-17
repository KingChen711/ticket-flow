const GATEWAY_URL = process.env.GATEWAY_URL ?? "http://localhost:8080";

// Server-side only — the Next.js server calls the gateway directly (BFF, see
// docs/phase-8-plan.md mục 2). No accessToken param here: these are the public routes
// (search/catalog browse), which never need one.
export async function fetchGateway<T>(path: string): Promise<T> {
  const res = await fetch(`${GATEWAY_URL}${path}`, { cache: "no-store" });
  if (!res.ok) {
    throw new Error(`Gateway ${path} responded ${res.status}`);
  }
  return res.json() as Promise<T>;
}
