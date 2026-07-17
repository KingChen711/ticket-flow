"use server";

import { redirect } from "next/navigation";
import type { HoldResult } from "@ticketflow/types";
import { auth } from "@/auth";

const GATEWAY_URL = process.env.GATEWAY_URL ?? "http://localhost:8080";

// Server Action — keeps the access token server-side (BFF, docs/phase-8-plan.md mục 2). The
// browser never sees it; this action calls the gateway on the signed-in user's behalf.
export async function holdSeatsAction(formData: FormData) {
  const session = await auth();
  if (!session?.accessToken) redirect("/api/auth/signin");

  const eventId = formData.get("eventId") as string;
  const seatIds = formData.getAll("seatIds") as string[];
  const totalAmount = Number(formData.get("totalAmount"));
  const currency = formData.get("currency") as string;

  if (seatIds.length === 0) return;

  const res = await fetch(`${GATEWAY_URL}/reservation/holds`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${session.accessToken}`,
    },
    body: JSON.stringify({ eventId, seatIds }),
  });

  if (!res.ok) throw new Error(`Gateway /reservation/holds responded ${res.status}`);
  const hold = (await res.json()) as HoldResult;

  if (!hold.success) {
    // Someone else grabbed a seat between page load and submit — send the user back to see
    // fresh availability rather than silently failing.
    redirect(`/events/${eventId}?holdFailed=1`);
  }

  const params = new URLSearchParams({
    eventId,
    holdId: hold.holdId,
    seatIds: seatIds.join(","),
    expiresAt: hold.expiresAt,
    totalAmount: String(totalAmount),
    currency,
  });
  redirect(`/checkout?${params.toString()}`);
}
