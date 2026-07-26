import Link from "next/link";
import { HttpError } from "@ticketflow/http-client";
import { formatEventDateTime, parseSearchEventsResult } from "@ticketflow/types";
import { fetchGateway } from "@/lib/gateway";

const PAGE_SIZE = 20;

export default async function HomePage({
  searchParams,
}: {
  searchParams: Promise<{ q?: string; city?: string; page?: string }>;
}) {
  const { q, city, page: pageValue } = await searchParams;
  const page = Math.max(1, Number.parseInt(pageValue ?? "1", 10) || 1);
  const params = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
  if (q) params.set("q", q);
  if (city) params.set("city", city);

  let result;
  try {
    result = await fetchGateway(`/search/events?${params}`, parseSearchEventsResult);
  } catch (error) {
    if (error instanceof HttpError && ["network", "timeout", "unavailable"].includes(error.kind)) {
      return <div className="mx-auto max-w-3xl p-6"><p role="status">Search is temporarily unavailable. Please retry shortly.</p></div>;
    }
    throw error;
  }

  const totalPages = Math.max(1, Math.ceil(result.total / PAGE_SIZE));
  const pageLink = (target: number) => {
    const link = new URLSearchParams();
    if (q) link.set("q", q);
    if (city) link.set("city", city);
    link.set("page", String(target));
    return `/?${link}`;
  };

  return (
    <div className="mx-auto max-w-3xl p-6">
      <form className="mb-6 flex gap-2" action="/">
        <input type="text" name="q" defaultValue={q} placeholder="Search by title or artist..." className="flex-1 rounded border px-3 py-2" />
        <input type="text" name="city" defaultValue={city} placeholder="City" className="w-40 rounded border px-3 py-2" />
        <button type="submit" className="rounded bg-black px-4 py-2 text-white">Search</button>
      </form>

      <p className="mb-3 text-sm text-gray-600">{result.total} event(s) · page {page} of {totalPages}</p>
      {result.items.length === 0 ? <p className="text-gray-500">No events found.</p> : (
        <ul className="flex flex-col gap-3">
          {result.items.map((event) => (
            <li key={event.eventId} className="rounded border p-4">
              <Link href={`/events/${event.eventId}`} className="text-lg font-semibold hover:underline">{event.title}</Link>
              <p className="text-sm text-gray-600">{event.artist} · {event.venueName}, {event.city}</p>
              <p className="text-sm text-gray-500">{formatEventDateTime(event.startsAt, event.timeZoneId)} · {event.status}</p>
            </li>
          ))}
        </ul>
      )}

      {totalPages > 1 && (
        <nav aria-label="Search pages" className="mt-5 flex items-center justify-between text-sm">
          {page > 1 ? <Link href={pageLink(page - 1)} className="text-blue-600 underline">Previous</Link> : <span />}
          {page < totalPages ? <Link href={pageLink(page + 1)} className="text-blue-600 underline">Next</Link> : <span />}
        </nav>
      )}
    </div>
  );
}
