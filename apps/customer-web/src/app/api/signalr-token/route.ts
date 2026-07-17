import { NextResponse } from "next/server";
import { auth } from "@/auth";

// The one deliberate exception to "token never leaves the server" (docs/phase-8-plan.md mục 2):
// the browser connects directly to the reservation hub via WebSocket for real-time seat updates,
// and SignalR's accessTokenFactory needs a token client-side to do that. This route hands it out
// on demand, memory-only on the client (never persisted), right before opening the connection.
export async function GET() {
  const session = await auth();
  if (!session?.accessToken) return NextResponse.json({ error: "unauthorized" }, { status: 401 });

  return NextResponse.json({ accessToken: session.accessToken });
}
