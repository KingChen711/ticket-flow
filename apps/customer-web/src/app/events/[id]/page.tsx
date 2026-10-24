import { HttpError } from "@ticketflow/http-client";
import { formatEventDateTime, parseEventDetailResponse } from "@ticketflow/types";
import { notFound } from "next/navigation";
import { auth } from "@/auth";
import { fetchGateway } from "@/lib/gateway";
import { SeatPicker } from "./SeatPicker";

export default async function EventDetailPage({
  params,
  searchParams,
}: {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ holdFailed?: string }>;
}) {
  const { id } = await params;
  const { holdFailed } = await searchParams;
  const session = await auth();

  let detail;
  try {
    detail = await fetchGateway(`/catalog/events/${id}/detail`, parseEventDetailResponse, {
      accessToken: session?.serverAccessToken,
    });
  } catch (error) {
    if (error instanceof HttpError && error.status === 404) notFound();
    throw error;
  }

  const availableSeats = detail.seats.filter((seat) => seat.status === "Available").length;
  const publicGatewayUrl = getPublicGatewayUrl();
  return (
    <div className="mx-auto max-w-3xl p-6">
      <h1 className="text-2xl font-bold">{detail.event.title}</h1>
      <p className="text-gray-600">{detail.event.artist}</p>
      <p className="text-gray-600">{detail.venue.name} — {detail.venue.street}, {detail.venue.city}, {detail.venue.country}</p>
      <p className="text-gray-600">
        {formatEventDateTime(detail.event.startsAt, detail.event.timeZoneId)} – {formatEventDateTime(detail.event.endsAt, detail.event.timeZoneId)}
      </p>
      <p className="mt-2">{detail.event.description}</p>

      <section className="mt-6">
        <h2 className="font-semibold">Price tiers</h2>
        <ul className="mt-1 flex flex-col gap-1 text-sm">
          {detail.event.priceTiers.map((tier) => <li key={tier.id}>{tier.amount.toLocaleString()} {tier.currency}</li>)}
        </ul>
      </section>

      <section className="mt-6">
        <h2 className="font-semibold">Seats</h2>
        {holdFailed && (
          <p role="alert" className="mb-3 text-sm text-red-700">
            {holdFailed === "rate-limited" ? "Too many hold attempts. Wait before trying again." :
              holdFailed === "conflict" ? "One or more selected seats are no longer available." :
                "Seat holds are temporarily unavailable."}
          </p>
        )}
        {detail.availabilityStatus === "TemporarilyUnavailable" ? (
          <p role="status" className="mt-2 rounded border border-amber-400 bg-amber-50 p-3 text-sm text-amber-900">
            Seat availability is temporarily unavailable. This event is not sold out; please retry shortly.
          </p>
        ) : (
          <>
            <p className="mb-3 text-sm text-gray-600">{availableSeats} of {detail.seats.length} seats available</p>
            <SeatPicker
              eventId={id}
              seats={detail.seats}
              priceTiers={detail.event.priceTiers}
              signedIn={!!session && !session.error}
              publicGatewayUrl={publicGatewayUrl}
            />
          </>
        )}
      </section>

      {detail.recommendations.length > 0 && (
        <section className="mt-6">
          <h2 className="font-semibold">You might also like</h2>
          <ul className="mt-1 flex flex-col gap-1 text-sm">
            {detail.recommendations.map((recommendation) => (
              <li key={recommendation.eventId}>{recommendation.title} — {recommendation.artist}, {recommendation.venueName}</li>
            ))}
          </ul>
        </section>
      )}
    </div>
  );
}

function getPublicGatewayUrl(): string {
  const configured = process.env.PUBLIC_GATEWAY_URL;
  if (!configured && process.env.NODE_ENV === "production") {
    throw new Error("Production requires PUBLIC_GATEWAY_URL.");
  }
  const url = new URL(configured ?? "http://localhost:8080");
  if (
    !["http:", "https:"].includes(url.protocol)
    || url.pathname !== "/"
    || url.search
    || url.hash
    || url.username
    || url.password
  ) {
    throw new Error("PUBLIC_GATEWAY_URL must be an absolute HTTP(S) origin.");
  }
  return url.origin;
}
