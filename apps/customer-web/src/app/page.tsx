import Link from "next/link";
import type { SearchEventsResult } from "@ticketflow/types";
import { fetchGateway } from "@/lib/gateway";

// Browse/search, no auth needed — the gateway route is public for GET (Phase 7 Commit 4).
// search-service (Elasticsearch-backed) serves both plain browsing (no filters) and filtered
// search (q/artist/city), so this page only calls it — catalog's own /events list would just be
// the same data without the search filters, no reason to fetch both (docs/phase-8-plan.md
// Commit 4 simplification).
export default async function HomePage({
  searchParams,
}: {
  searchParams: Promise<{ q?: string; city?: string }>;
}) {
  const { q, city } = await searchParams;
  const params = new URLSearchParams();
  if (q) params.set("q", q);
  if (city) params.set("city", city);

  const result = await fetchGateway<SearchEventsResult>(`/search/events?${params.toString()}`);

  return (
    <div className="mx-auto max-w-3xl p-6">
      <form className="mb-6 flex gap-2" action="/">
        <input
          type="text"
          name="q"
          defaultValue={q}
          placeholder="Search by title or artist..."
          className="flex-1 rounded border px-3 py-2"
        />
        <input
          type="text"
          name="city"
          defaultValue={city}
          placeholder="City"
          className="w-40 rounded border px-3 py-2"
        />
        <button type="submit" className="rounded bg-black px-4 py-2 text-white">
          Search
        </button>
      </form>

      {result.items.length === 0 ? (
        <p className="text-gray-500">No events found.</p>
      ) : (
        <ul className="flex flex-col gap-3">
          {result.items.map((event) => (
            <li key={event.eventId} className="rounded border p-4">
              <Link href={`/events/${event.eventId}`} className="text-lg font-semibold hover:underline">
                {event.title}
              </Link>
              <p className="text-sm text-gray-600">
                {event.artist} · {event.venueName}, {event.city}
              </p>
              <p className="text-sm text-gray-500">
                {new Date(event.startsAt).toLocaleString()} · {event.status}
              </p>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
