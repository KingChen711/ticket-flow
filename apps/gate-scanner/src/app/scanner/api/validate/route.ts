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
  if (
    typeof body !== "object" ||
    body === null ||
    typeof (body as { qrPayload?: unknown }).qrPayload !== "string" ||
    !isUuid((body as { eventId?: unknown }).eventId) ||
    !isGateId((body as { gateId?: unknown }).gateId)
  ) {
    return NextResponse.json({ error: "qrPayload, eventId and gateId are required" }, { status: 400 });
  }

  try {
    const result = await fetchJson(`${GATEWAY_URL}/ticketing/tickets/validate`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${session.serverAccessToken}`,
      },
      // Ticketing owns the gate assignment check: the expected event is not a browser hint. It
      // is compared to the ticket event and the caller's persisted assignment to this gate.
      body: JSON.stringify({
        qrPayload: (body as { qrPayload: string }).qrPayload,
        eventId: (body as { eventId: string }).eventId,
        gateId: (body as { gateId: string }).gateId,
      }),
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

function isUuid(value: unknown): value is string {
  return typeof value === "string" &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value);
}

function isGateId(value: unknown): value is string {
  return typeof value === "string" && value.trim().length > 0 && value.trim().length <= 80;
}
