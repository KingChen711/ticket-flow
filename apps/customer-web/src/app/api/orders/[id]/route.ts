import { HttpError } from "@ticketflow/http-client";
import { parseMyOrderView } from "@ticketflow/types";
import { NextResponse } from "next/server";
import { auth } from "@/auth";
import { fetchGateway } from "@/lib/gateway";

export async function GET(request: Request, { params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const session = await auth();
  if (!session?.serverAccessToken || session.error) return NextResponse.json({ error: "reauthentication required" }, { status: 401 });

  try {
    const order = await fetchGateway(`/ordering/orders/${id}`, parseMyOrderView, {
      accessToken: session.serverAccessToken,
      signal: request.signal,
      timeoutMs: 5_000,
    });
    return NextResponse.json(order);
  } catch (error) {
    const status = error instanceof HttpError ? (error.status ?? 503) : 500;
    return NextResponse.json({ error: error instanceof Error ? error.message : "order lookup failed" }, { status });
  }
}
