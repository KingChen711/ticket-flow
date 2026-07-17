import { NextResponse } from "next/server";
import { auth } from "@/auth";

const GATEWAY_URL = process.env.GATEWAY_URL ?? "http://localhost:8080";

// BFF proxy — the order-status client component polls this same-origin route instead of
// calling the gateway directly, because the access token lives server-side only (session
// cookie), never in browser JS (docs/phase-8-plan.md mục 2).
export async function GET(_request: Request, { params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const session = await auth();
  if (!session?.accessToken) return NextResponse.json({ error: "unauthorized" }, { status: 401 });

  const res = await fetch(`${GATEWAY_URL}/ordering/orders/${id}`, {
    headers: { Authorization: `Bearer ${session.accessToken}` },
    cache: "no-store",
  });

  return NextResponse.json(await res.json(), { status: res.status });
}
