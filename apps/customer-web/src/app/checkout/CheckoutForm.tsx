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

  const methods = [
    { value: "credit-card", label: "Credit card" },
    { value: "e-wallet", label: "E-wallet" },
  ];

  return (
    <form action={action} className="flex flex-col gap-4" aria-busy={pending}>
      <input type="hidden" name="holdId" value={holdId} />
      <input type="hidden" name="idempotencyKey" value={idempotencyKey} />

      <fieldset className="flex flex-col gap-2">
        <legend className="label mb-2">Payment method</legend>
        <div className="grid gap-2 sm:grid-cols-2">
          {methods.map((method) => (
            <label
              key={method.value}
              className="flex min-h-11 cursor-pointer items-center gap-3 rounded-lg border border-border-strong px-3 text-sm has-[:checked]:border-primary has-[:checked]:bg-primary-soft"
            >
              <input
                type="radio"
                name="paymentMethod"
                value={method.value}
                defaultChecked={method.value === "credit-card"}
                className="size-4 accent-[var(--primary)]"
              />
              {method.label}
            </label>
          ))}
        </div>
        <p className="hint">Payments in this demo are simulated; no card is charged.</p>
      </fieldset>

      {result.kind === "conflict" && (
        <p role="alert" className="alert-warning">
          This request key is already bound to different checkout details. {result.message}
        </p>
      )}
      {result.kind === "error" && (
        <p role="alert" className="alert-danger">
          {result.message}
        </p>
      )}

      <button type="submit" disabled={pending || result.kind === "success"} className="btn-primary w-full">
        {pending || result.kind === "success" ? "Placing order…" : "Place order"}
      </button>
    </form>
  );
}
