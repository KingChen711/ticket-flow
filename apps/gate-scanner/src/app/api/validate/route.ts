import { fetchJson, HttpError } from "@ticketflow/http-client";
import { parseValidateTicketResponse } from "@ticketflow/types";
import { NextResponse } from "next/server";
import { auth } from "@/auth";

const GATEWAY_URL = process.env.GATEWAY_URL ?? "http://localhost:8080";

export async function POST(request: Request) {
  const session = await auth();
  if (!session?.serverAccessToken || session.error) {
    return NextResponse.json({ error: "Reauthentication required" }, { status: 401 });
  }

  let body: unknown;
  try {
    body = await request.json();
  } catch {
    return NextResponse.json({ error: "Invalid JSON" }, { status: 400 });
  }
  if (typeof body !== "object" || body === null || typeof (body as { qrPayload?: unknown }).qrPayload !== "string") {
    return NextResponse.json({ error: "qrPayload is required" }, { status: 400 });
  }

  try {
    const result = await fetchJson(`${GATEWAY_URL}/ticketing/tickets/validate`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${session.serverAccessToken}`,
      },
      body: JSON.stringify({ qrPayload: (body as { qrPayload: string }).qrPayload }),
      signal: request.signal,
      timeoutMs: 5_000,
      validate: parseValidateTicketResponse,
    });
    return NextResponse.json(result);
  } catch (error) {
    const status = error instanceof HttpError ? (error.status ?? 503) : 500;
    return NextResponse.json({ error: error instanceof Error ? error.message : "validation failed" }, { status });
  }
}
