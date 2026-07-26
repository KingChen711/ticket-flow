"use client";

import { useActionState, useEffect } from "react";
import { useRouter } from "next/navigation";
import { placeOrderAction, type CheckoutActionResult } from "./actions";

const initialState: CheckoutActionResult = { kind: "idle" };

export function CheckoutForm({ holdId, idempotencyKey }: { holdId: string; idempotencyKey: string }) {
  const [result, action, pending] = useActionState(placeOrderAction, initialState);
  const router = useRouter();

  useEffect(() => {
    if (result.kind === "success") router.replace(`/orders/${result.orderId}`);
  }, [result, router]);

  return (
    <form action={action} className="mt-6 flex flex-col gap-3">
      <input type="hidden" name="holdId" value={holdId} />
      <input type="hidden" name="idempotencyKey" value={idempotencyKey} />

      <label className="flex flex-col gap-1 text-sm">
        Payment method
        <select name="paymentMethod" defaultValue="credit-card" className="rounded border px-3 py-2">
          <option value="credit-card">Credit card</option>
          <option value="e-wallet">E-wallet</option>
        </select>
      </label>

      {result.kind === "conflict" && (
        <p role="alert" className="rounded border border-amber-400 bg-amber-50 p-3 text-sm text-amber-900">
          This request key is already bound to different checkout details. {result.message}
        </p>
      )}
      {result.kind === "error" && (
        <p role="alert" className="rounded border border-red-400 bg-red-50 p-3 text-sm text-red-700">
          {result.message}
        </p>
      )}

      <button type="submit" disabled={pending || result.kind === "success"} className="rounded bg-black px-4 py-2 text-white disabled:opacity-40">
        {pending || result.kind === "success" ? "Placing order…" : "Place order"}
      </button>
    </form>
  );
}
