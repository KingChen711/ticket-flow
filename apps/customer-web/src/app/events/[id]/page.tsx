import { notFound } from "next/navigation";
import type { EventDetailResponse } from "@ticketflow/types";
import { auth } from "@/auth";

const GATEWAY_URL = process.env.GATEWAY_URL ?? "http://localhost:8080";

// Uses the Phase 6 aggregator endpoint (event + venue + seat availability + recommendations in
// one request) — exactly the shape it was built for. Public route, but passes userId when
// signed in so recommendations are personalized instead of falling back to popular-events.
export default async function EventDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const session = await auth();
  const query = session?.userId ? `?userId=${session.userId}` : "";

  const res = await fetch(`${GATEWAY_URL}/catalog/events/${id}/detail${query}`, { cache: "no-store" });
  if (res.status === 404) notFound();
  if (!res.ok) throw new Error(`Gateway /catalog/events/${id}/detail responded ${res.status}`);

  const detail = (await res.json()) as EventDetailResponse;
  const availableSeats = detail.seats.filter((s) => s.status === "Available").length;

  return (
    <div className="mx-auto max-w-3xl p-6">
      <h1 className="text-2xl font-bold">{detail.event.title}</h1>
      <p className="text-gray-600">{detail.event.artist}</p>
      <p className="text-gray-600">
        {detail.venue.name} — {detail.venue.street}, {detail.venue.city}, {detail.venue.country}
      </p>
      <p className="text-gray-600">
        {new Date(detail.event.startsAt).toLocaleString()} – {new Date(detail.event.endsAt).toLocaleString()}
      </p>
      <p className="mt-2">{detail.event.description}</p>

      <section className="mt-6">
        <h2 className="font-semibold">Price tiers</h2>
        <ul className="mt-1 flex flex-col gap-1 text-sm">
          {detail.event.priceTiers.map((tier) => (
            <li key={tier.id}>
              {tier.amount.toLocaleString()} {tier.currency}
            </li>
          ))}
        </ul>
      </section>

      <section className="mt-6">
        <h2 className="font-semibold">Seats</h2>
        <p className="text-sm text-gray-600">
          {availableSeats} of {detail.seats.length} seats available
        </p>
      </section>

      {detail.recommendations.length > 0 && (
        <section className="mt-6">
          <h2 className="font-semibold">You might also like</h2>
          <ul className="mt-1 flex flex-col gap-1 text-sm">
            {detail.recommendations.map((rec) => (
              <li key={rec.eventId}>
                {rec.title} — {rec.artist}, {rec.venueName}
              </li>
            ))}
          </ul>
        </section>
      )}
    </div>
  );
}
