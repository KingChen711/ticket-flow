const GATEWAY_URL = process.env.GATEWAY_URL ?? "http://localhost:8080";

// Server-side only — this Next.js server calls the gateway directly (BFF, docs/phase-8-plan.md
// mục 2). Used for the public catalog GET routes (listing venues/events for the create-event
// form and dashboard) that don't need a token.
export async function fetchGateway<T>(path: string): Promise<T> {
  const res = await fetch(`${GATEWAY_URL}${path}`, { cache: "no-store" });
  if (!res.ok) {
    throw new Error(`Gateway ${path} responded ${res.status}`);
  }
  return res.json() as Promise<T>;
}

// Authenticated variant — organizer write operations (create venue/event/seatmap/..., revenue).
export async function fetchGatewayAuthed<T>(
  path: string,
  accessToken: string,
  init?: RequestInit,
): Promise<T> {
  const res = await fetch(`${GATEWAY_URL}${path}`, {
    ...init,
    cache: "no-store",
    headers: {
      ...init?.headers,
      Authorization: `Bearer ${accessToken}`,
    },
  });
  if (!res.ok) {
    throw new Error(`Gateway ${path} responded ${res.status}`);
  }
  return res.json() as Promise<T>;
}
