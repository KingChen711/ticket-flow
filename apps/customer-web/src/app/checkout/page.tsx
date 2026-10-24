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
    <div className="mx-auto max-w-md p-6">
      <h1 className="text-xl font-bold">Checkout</h1>
      <Countdown expiresAt={quote.expiresAt} />
      <p className="mt-4">
        {quote.seatIds.length} seat(s) — {quote.amount.toLocaleString()} {quote.currency}
      </p>
      <CheckoutForm holdId={quote.holdId} idempotencyKey={idempotencyKey} />
    </div>
  );
}
