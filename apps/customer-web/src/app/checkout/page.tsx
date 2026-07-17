import { redirect } from "next/navigation";
import { Countdown } from "./Countdown";
import { placeOrderAction } from "./actions";

export default async function CheckoutPage({
  searchParams,
}: {
  searchParams: Promise<{
    eventId?: string;
    holdId?: string;
    seatIds?: string;
    expiresAt?: string;
    totalAmount?: string;
    currency?: string;
  }>;
}) {
  const { eventId, holdId, seatIds, expiresAt, totalAmount, currency } = await searchParams;
  if (!eventId || !holdId || !seatIds || !expiresAt || !totalAmount || !currency) {
    redirect("/");
  }

  const seatCount = seatIds.split(",").length;

  return (
    <div className="mx-auto max-w-md p-6">
      <h1 className="text-xl font-bold">Checkout</h1>
      <Countdown expiresAt={expiresAt} />

      <p className="mt-4">
        {seatCount} seat(s) — {Number(totalAmount).toLocaleString()} {currency}
      </p>

      <form action={placeOrderAction} className="mt-6 flex flex-col gap-3">
        <input type="hidden" name="eventId" value={eventId} />
        <input type="hidden" name="holdId" value={holdId} />
        <input type="hidden" name="seatIds" value={seatIds} />
        <input type="hidden" name="totalAmount" value={totalAmount} />
        <input type="hidden" name="currency" value={currency} />

        <label className="flex flex-col gap-1 text-sm">
          Payment method
          <select name="paymentMethod" defaultValue="credit-card" className="rounded border px-3 py-2">
            <option value="credit-card">Credit card</option>
            <option value="e-wallet">E-wallet</option>
          </select>
        </label>

        <button type="submit" className="rounded bg-black px-4 py-2 text-white">
          Place order
        </button>
      </form>
    </div>
  );
}
