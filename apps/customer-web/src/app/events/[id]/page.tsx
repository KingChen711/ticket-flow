import { HttpError } from "@ticketflow/http-client";
import { formatEventDateTime, parseEventDetailResponse } from "@ticketflow/types";
import Link from "next/link";
import { notFound } from "next/navigation";
import { auth } from "@/auth";
import { fetchGateway } from "@/lib/gateway";
import { eventBadge, formatMoney, statusLabel } from "@/lib/status";
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
    <div className="container-page">
      <Link href="/" className="link text-sm">← All events</Link>

      <header className="mt-4 flex flex-col gap-3">
        <div className="flex flex-wrap items-center gap-3">
          <h1 className="text-3xl font-bold tracking-tight">{detail.event.title}</h1>
          <span className={eventBadge(detail.event.status)}>{statusLabel(detail.event.status)}</span>
        </div>
        <p className="text-lg text-muted">{detail.event.artist}</p>
        <dl className="grid gap-2 text-sm sm:grid-cols-2">
          <div className="flex gap-2">
            <dt><CalendarIcon /><span className="sr-only">When</span></dt>
            <dd>
              {formatEventDateTime(detail.event.startsAt, detail.event.timeZoneId)} – {formatEventDateTime(detail.event.endsAt, detail.event.timeZoneId)}
            </dd>
          </div>
          <div className="flex gap-2">
            <dt><PinIcon /><span className="sr-only">Where</span></dt>
            <dd>{detail.venue.name}, {detail.venue.street}, {detail.venue.city}, {detail.venue.country}</dd>
          </div>
        </dl>
        <p className="max-w-prose">{detail.event.description}</p>
      </header>

      <div className="mt-8 grid gap-6 lg:grid-cols-[1fr_16rem]">
        <section aria-labelledby="seats-heading" className="card">
          <h2 id="seats-heading" className="text-lg font-semibold">Choose your seats</h2>
          {holdFailed && (
            <p role="alert" className="alert-danger mt-3">
              {holdFailed === "rate-limited" ? "Too many hold attempts. Wait before trying again." :
                holdFailed === "conflict" ? "One or more selected seats are no longer available." :
                  "Seat holds are temporarily unavailable."}
            </p>
          )}
          {detail.availabilityStatus === "TemporarilyUnavailable" ? (
            <p role="status" className="alert-warning mt-3">
              Seat availability is temporarily unavailable. This event is not sold out; please retry shortly.
            </p>
          ) : (
            <>
              <p className="mb-4 mt-1 text-sm text-muted">{availableSeats} of {detail.seats.length} seats available</p>
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

        <aside className="flex flex-col gap-6">
          <section aria-labelledby="prices-heading" className="card">
            <h2 id="prices-heading" className="font-semibold">Price tiers</h2>
            <ul className="mt-3 flex flex-col gap-2 text-sm">
              {detail.event.priceTiers.map((tier, index) => (
                <li key={tier.id} className="flex items-center justify-between">
                  <span className="text-muted">Section {index + 1}</span>
                  <span className="font-semibold">{formatMoney(tier.amount, tier.currency)}</span>
                </li>
              ))}
            </ul>
          </section>

          {detail.recommendations.length > 0 && (
            <section aria-labelledby="recommended-heading" className="card">
              <h2 id="recommended-heading" className="font-semibold">You might also like</h2>
              <ul className="mt-3 flex flex-col gap-3 text-sm">
                {detail.recommendations.map((recommendation) => (
                  <li key={recommendation.eventId}>
                    <Link href={`/events/${recommendation.eventId}`} className="link">{recommendation.title}</Link>
                    <p className="text-muted">{recommendation.artist} · {recommendation.venueName}</p>
                  </li>
                ))}
              </ul>
            </section>
          )}
        </aside>
      </div>
    </div>
  );
}

function CalendarIcon() {
  return (
    <svg aria-hidden="true" viewBox="0 0 24 24" className="mt-0.5 size-4 text-muted" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round">
      <rect x="3" y="5" width="18" height="16" rx="2" />
      <path d="M16 3v4M8 3v4M3 11h18" />
    </svg>
  );
}

function PinIcon() {
  return (
    <svg aria-hidden="true" viewBox="0 0 24 24" className="mt-0.5 size-4 text-muted" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
      <path d="M12 21s-7-6.2-7-12a7 7 0 0 1 14 0c0 5.8-7 12-7 12Z" />
      <circle cx="12" cy="9" r="2.5" />
    </svg>
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
