"use server";

import { randomUUID } from "node:crypto";
import { HttpError } from "@ticketflow/http-client";
import { parseHoldResult } from "@ticketflow/types";
import { redirect } from "next/navigation";
import { auth } from "@/auth";
import { mutateGateway } from "@/lib/gateway";

export async function holdSeatsAction(formData: FormData) {
  const session = await auth();
  if (!session?.serverAccessToken || session.error === "RefreshTokenError") {
    redirect("/api/auth/signin");
  }

  const eventId = formData.get("eventId")?.toString() ?? "";
  const seatIds = formData.getAll("seatIds").map(String);
  if (!eventId || seatIds.length === 0) return;

  let hold;
  try {
    hold = await mutateGateway(
      "/reservation/holds",
      session.serverAccessToken,
      { eventId, seatIds },
      parseHoldResult,
    );
  } catch (error) {
    if (error instanceof HttpError && error.details) {
      try {
        hold = parseHoldResult(error.details);
      } catch {
        // Fall through to the generic degraded result below.
      }
    }
    if (!hold) redirect(`/events/${eventId}?holdFailed=unavailable`);
  }

  if (!hold.success || !hold.quote) {
    const reason = hold.reason === "RateLimited" ? "rate-limited" : "conflict";
    redirect(`/events/${eventId}?holdFailed=${reason}`);
  }

  redirect(`/checkout?${new URLSearchParams({
    holdId: hold.quote.holdId,
    idempotencyKey: randomUUID(),
  })}`);
}
