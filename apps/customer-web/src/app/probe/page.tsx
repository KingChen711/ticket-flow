import { auth } from "@/auth";

// Phase 8 Commit 3 probe — proves 2 real calls through the gateway from the Next.js server:
// a public route with no token, and an authenticated route using the session's access token.
export default async function ProbePage() {
  const session = await auth();
  const gatewayUrl = process.env.GATEWAY_URL ?? "http://localhost:8080";

  const publicRes = await fetch(`${gatewayUrl}/catalog/venues`, { cache: "no-store" });
  const publicResult = { status: publicRes.status, ok: publicRes.ok };

  let authedResult: { status: number; ok: boolean; body?: unknown } = { status: 0, ok: false };
  if (session?.accessToken) {
    const authedRes = await fetch(`${gatewayUrl}/ordering/orders`, {
      cache: "no-store",
      headers: { Authorization: `Bearer ${session.accessToken}` },
    });
    authedResult = { status: authedRes.status, ok: authedRes.ok, body: await authedRes.json() };
  }

  return (
    <main className="flex min-h-screen flex-col gap-4 p-8">
      <h1 className="text-xl font-bold">Gateway probe</h1>
      <section>
        <h2 className="font-semibold">GET /catalog/venues (public, no token)</h2>
        <pre className="bg-gray-100 p-2 text-sm">{JSON.stringify(publicResult, null, 2)}</pre>
      </section>
      <section>
        <h2 className="font-semibold">GET /ordering/orders (authenticated, session accessToken)</h2>
        <pre className="bg-gray-100 p-2 text-sm">{JSON.stringify(authedResult, null, 2)}</pre>
      </section>
      <a href="/" className="text-blue-600 underline">
        Back
      </a>
    </main>
  );
}
