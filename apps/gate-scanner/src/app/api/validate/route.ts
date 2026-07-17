import { NextResponse } from "next/server";
import type { ValidateTicketResponse } from "@ticketflow/types";
import { auth } from "@/auth";

const GATEWAY_URL = process.env.GATEWAY_URL ?? "http://localhost:8080";

// BFF proxy (docs/phase-8-plan.md mục 2) — ScannerClient is a client component (needs the
// camera/canvas APIs) so it can't hold the session's accessToken itself; this route reads the
// session server-side and forwards to the gateway on the signed-in organizer's behalf.
export async function POST(request: Request) {
  const session = await auth();
  if (!session?.accessToken) {
    return NextResponse.json({ error: "Not signed in" }, { status: 401 });
  }

  const body = await request.json();

  const res = await fetch(`${GATEWAY_URL}/ticketing/tickets/validate`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${session.accessToken}`,
    },
    body: JSON.stringify({ qrPayload: body.qrPayload }),
  });

  if (!res.ok) {
    return NextResponse.json({ error: `Gateway responded ${res.status}` }, { status: res.status });
  }

  const result = (await res.json()) as ValidateTicketResponse;
  return NextResponse.json(result);
}
