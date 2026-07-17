"use server";

import { redirect } from "next/navigation";
import type { OrderPlacedResponse } from "@ticketflow/types";
import { auth } from "@/auth";

const GATEWAY_URL = process.env.GATEWAY_URL ?? "http://localhost:8080";

// Server Action — POST /ordering/orders body carries only order data (catalogEventId, seatIds,
// totalAmount, currency, holdId, paymentMethod). No customerId/name/email: the gateway forwards
// the Bearer token and ordering reads identity from its claims (Phase 7 Commit 4).
export async function placeOrderAction(formData: FormData) {
  const session = await auth();
  if (!session?.accessToken) redirect("/api/auth/signin");

  const body = {
    catalogEventId: formData.get("eventId") as string,
    seatIds: (formData.get("seatIds") as string).split(","),
    totalAmount: Number(formData.get("totalAmount")),
    currency: formData.get("currency") as string,
    holdId: formData.get("holdId") as string,
    paymentMethod: formData.get("paymentMethod") as string,
  };

  const res = await fetch(`${GATEWAY_URL}/ordering/orders`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${session.accessToken}`,
    },
    body: JSON.stringify(body),
  });

  if (!res.ok) throw new Error(`Gateway /ordering/orders responded ${res.status}`);
  const order = (await res.json()) as OrderPlacedResponse;

  redirect(`/orders/${order.orderId}`);
}
