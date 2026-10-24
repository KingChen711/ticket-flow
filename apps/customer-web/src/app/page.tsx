import Link from "next/link";
import { HttpError } from "@ticketflow/http-client";
import { formatEventDateTime, parseSearchEventsResult } from "@ticketflow/types";
import { fetchGateway } from "@/lib/gateway";
import { eventBadge, statusLabel } from "@/lib/status";

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
      return (
        <div className="container-page">
          <p role="status" className="alert-warning">Search is temporarily unavailable. Please retry shortly.</p>
        </div>
      );
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
    <div className="container-page">
      <section className="mb-8">
        <h1 className="text-3xl font-bold tracking-tight sm:text-4xl">Find your next live event</h1>
        <p className="mt-2 text-muted">Concerts and shows, with seats held for you while you check out.</p>
        <form role="search" className="card mt-6 grid gap-3 p-4 sm:grid-cols-[1fr_12rem_auto] sm:items-end" action="/">
          <div className="field">
            <label htmlFor="search-q" className="label">Event or artist</label>
            <input id="search-q" type="search" name="q" defaultValue={q} placeholder="e.g. Jazz night" className="input" />
          </div>
          <div className="field">
            <label htmlFor="search-city" className="label">City</label>
            <input id="search-city" type="text" name="city" defaultValue={city} placeholder="Any city" className="input" />
          </div>
          <button type="submit" className="btn-primary">
            <SearchIcon />
            Search
          </button>
        </form>
      </section>

      <p className="mb-4 text-sm text-muted" aria-live="polite">
        {result.total} event(s) · page {page} of {totalPages}
      </p>
      {result.items.length === 0 ? (
        <div className="card text-center text-muted">
          <p className="font-medium text-fg">No events found.</p>
          <p className="mt-1 text-sm">Try another artist or clear the city filter.</p>
        </div>
      ) : (
        <ul className="grid gap-4 sm:grid-cols-2">
          {result.items.map((event) => (
            <li key={event.eventId}>
              <Link
                href={`/events/${event.eventId}`}
                className="card group flex h-full flex-col gap-2 transition-colors duration-150 hover:border-primary"
              >
                <div className="flex items-start justify-between gap-3">
                  <h2 className="text-lg font-semibold group-hover:text-primary">{event.title}</h2>
                  <span className={eventBadge(event.status)}>{statusLabel(event.status)}</span>
                </div>
                <p className="text-sm text-muted">{event.artist}</p>
                <p className="mt-auto text-sm">
                  {formatEventDateTime(event.startsAt, event.timeZoneId)}
                  <span className="text-muted"> · {event.venueName}, {event.city}</span>
                </p>
              </Link>
            </li>
          ))}
        </ul>
      )}

      {totalPages > 1 && (
        <nav aria-label="Search pages" className="mt-8 flex items-center justify-between">
          {page > 1 ? <Link href={pageLink(page - 1)} className="btn-secondary">Previous</Link> : <span />}
          {page < totalPages ? <Link href={pageLink(page + 1)} className="btn-secondary">Next</Link> : <span />}
        </nav>
      )}
    </div>
  );
}

function SearchIcon() {
  return (
    <svg aria-hidden="true" viewBox="0 0 24 24" className="size-4" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round">
      <circle cx="11" cy="11" r="7" />
      <path d="m20 20-3.5-3.5" />
    </svg>
  );
}
