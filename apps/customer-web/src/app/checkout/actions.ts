"use server";

import { HttpError } from "@ticketflow/http-client";
import { parseOrderPlacedResponse } from "@ticketflow/types";
import { redirect } from "next/navigation";
import { auth } from "@/auth";
import { mutateGateway } from "@/lib/gateway";

export type CheckoutActionResult =
  | { kind: "idle" }
  | { kind: "success"; orderId: string }
  | { kind: "conflict"; message: string }
  | { kind: "error"; message: string };

export async function placeOrderAction(
  _previous: CheckoutActionResult,
  formData: FormData,
): Promise<CheckoutActionResult> {
  const session = await auth();
  if (!session?.serverAccessToken || session.error === "RefreshTokenError") {
    redirect("/api/auth/signin?callbackUrl=/checkout");
  }

  const holdId = formData.get("holdId")?.toString() ?? "";
  const paymentMethod = formData.get("paymentMethod")?.toString() ?? "";
  const idempotencyKey = formData.get("idempotencyKey")?.toString() ?? "";
  if (!holdId || !paymentMethod || !idempotencyKey) {
    return { kind: "error", message: "Checkout details are incomplete. Reload and try again." };
  }

  try {
    const order = await mutateGateway(
      "/ordering/orders",
      session.serverAccessToken,
      { holdId, paymentMethod, idempotencyKey },
      parseOrderPlacedResponse,
    );
    return { kind: "success", orderId: order.orderId };
  } catch (error) {
    if (error instanceof HttpError && error.kind === "conflict") {
      return { kind: "conflict", message: error.message };
    }
    if (error instanceof HttpError && (error.kind === "timeout" || error.kind === "unavailable" || error.kind === "network")) {
      return {
        kind: "error",
        message: "Checkout is temporarily unavailable. Retry safely with the same request key.",
      };
    }
    return { kind: "error", message: "Checkout failed. Reload the hold before trying again." };
  }
}
