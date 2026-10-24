import { randomUUID } from "node:crypto";
import { HttpError } from "@ticketflow/http-client";
import { parseHoldQuote } from "@ticketflow/types";
import { notFound, redirect } from "next/navigation";
import { auth } from "@/auth";
import { fetchGateway } from "@/lib/gateway";
import { CheckoutForm } from "./CheckoutForm";
import { Countdown } from "./Countdown";

export default async function CheckoutPage({
  searchParams,
}: {
  searchParams: Promise<{ holdId?: string; idempotencyKey?: string }>;
}) {
  const { holdId, idempotencyKey } = await searchParams;
  if (!holdId) redirect("/");

  if (!idempotencyKey || idempotencyKey.length > 200) {
    const stableKey = randomUUID();
    redirect(`/checkout?${new URLSearchParams({ holdId, idempotencyKey: stableKey })}`);
  }

  const session = await auth();
  if (!session?.serverAccessToken || session.error === "RefreshTokenError") {
    const callbackUrl = `/checkout?${new URLSearchParams({ holdId, idempotencyKey })}`;
    redirect(`/api/auth/signin?callbackUrl=${encodeURIComponent(callbackUrl)}`);
  }

  let quote;
  try {
    quote = await fetchGateway(
      `/reservation/holds/${encodeURIComponent(holdId)}/quote`,
      parseHoldQuote,
      { accessToken: session.serverAccessToken },
    );
  } catch (error) {
    if (error instanceof HttpError && error.status === 404) notFound();
    throw error;
  }

  return (
    <div className="container-page max-w-lg">
      <h1 className="text-2xl font-bold tracking-tight">Checkout</h1>
      <div className="card mt-6 flex flex-col gap-4">
        <Countdown expiresAt={quote.expiresAt} />
        <dl className="flex flex-col gap-2 border-t border-border pt-4 text-sm">
          <div className="flex justify-between">
            <dt className="text-muted">Seats</dt>
            <dd className="font-medium">{quote.seatIds.length}</dd>
          </div>
          <div className="flex justify-between text-base">
            <dt className="font-semibold">Total</dt>
            <dd className="font-bold">{quote.amount.toLocaleString()} {quote.currency}</dd>
          </div>
          <p className="hint">Price confirmed by the server when your seats were held. No extra fees.</p>
        </dl>
        <CheckoutForm holdId={quote.holdId} idempotencyKey={idempotencyKey} />
      </div>
    </div>
  );
}
