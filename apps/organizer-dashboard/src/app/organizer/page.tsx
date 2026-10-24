import Link from "next/link";
import { formatEventDateTime, parseEventResponses, parseRevenueResult } from "@ticketflow/types";
import { auth } from "@/auth";
import { fetchGatewayAuthed } from "@/lib/gateway";

// Role gate lives here, not just at the backend (docs/phase-8-plan.md Commit 7 — "customer login
// vào app này phải bị chặn ở UI lẫn API"). GET /analytics/revenue already 403s a non-organizer
// token at the backend (RequireOrganizer, Phase 7 Commit 2); this page also refuses to render the
// dashboard for a signed-in customer instead of letting that 403 surface as a raw fetch error.
export default async function DashboardPage() {
  const session = await auth();

  if (!session || session.error || !session.serverAccessToken) {
    return (
      <div className="container-page max-w-xl">
        <div className="card text-center">
          <h1 className="text-xl font-semibold">Organizer dashboard</h1>
          <p className="mt-2 text-muted">Sign in with an organizer account to manage events.</p>
        </div>
      </div>
    );
  }

  if (!session.roles?.includes("organizer")) {
    return (
      <div className="container-page max-w-xl">
        <p role="alert" className="alert-danger">
          This app is for organizers only. Your account does not have the organizer role.
        </p>
      </div>
    );
  }

  const [events, revenue] = await Promise.all([
    fetchGatewayAuthed("/catalog/events", session.serverAccessToken, parseEventResponses),
    fetchGatewayAuthed("/analytics/revenue", session.serverAccessToken, parseRevenueResult),
  ]);

  const titleByEventId = new Map(events.map((e) => [e.id, e.title]));

  const totalLabel = revenue.totalRevenue !== null
    ? `${revenue.totalRevenue.toLocaleString()} ${revenue.byCurrency[0]?.currency ?? ""}`.trim()
    : `${revenue.byCurrency.length} currencies`;

  return (
    <div className="container-page">
      <h1 className="text-2xl font-bold tracking-tight">Dashboard</h1>

      <section aria-labelledby="revenue-heading" className="mt-6">
        <h2 id="revenue-heading" className="sr-only">Revenue</h2>
        <dl className="grid gap-4 sm:grid-cols-3">
          <div className="card">
            <dt className="text-sm text-muted">Revenue</dt>
            <dd className="mt-1 text-2xl font-bold">{revenue.totalSeats === 0 ? "No revenue yet." : totalLabel}</dd>
          </div>
          <div className="card">
            <dt className="text-sm text-muted">Seats sold</dt>
            <dd className="mt-1 text-2xl font-bold">{revenue.totalSeats.toLocaleString()}</dd>
          </div>
          <div className="card">
            <dt className="text-sm text-muted">Events</dt>
            <dd className="mt-1 text-2xl font-bold">{events.length}</dd>
          </div>
        </dl>

        {revenue.totalRevenue === null && revenue.byCurrency.length > 0 && (
          <ul className="card mt-4 flex flex-col gap-1 text-sm">
            {revenue.byCurrency.map((currency) => (
              <li key={currency.currency} className="flex justify-between">
                <span className="text-muted">{currency.currency}</span>
                <span>{currency.revenue.toLocaleString()} · {currency.seatCount} seats</span>
              </li>
            ))}
          </ul>
        )}

        {revenue.byEvent.length > 0 && (
          <div className="card mt-4 overflow-x-auto p-0">
            <table className="w-full text-left text-sm">
              <caption className="px-5 pt-4 text-left font-semibold">Revenue by event</caption>
              <thead className="text-muted">
                <tr className="border-b border-border">
                  <th scope="col" className="px-5 py-3 font-medium">Event</th>
                  <th scope="col" className="px-5 py-3 text-right font-medium">Revenue</th>
                  <th scope="col" className="px-5 py-3 text-right font-medium">Seats</th>
                </tr>
              </thead>
              <tbody>
                {revenue.byEvent.map((r) => (
                  <tr key={`${r.eventId}:${r.currency}`} className="border-b border-border last:border-0">
                    <td className="px-5 py-3">{titleByEventId.get(r.eventId) ?? r.eventId}</td>
                    <td className="px-5 py-3 text-right tabular-nums">{r.revenue.toLocaleString()} {r.currency}</td>
                    <td className="px-5 py-3 text-right tabular-nums">{r.seatCount}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <section aria-labelledby="events-heading" className="mt-10">
        <div className="mb-4 flex items-center justify-between">
          <h2 id="events-heading" className="text-lg font-semibold">Events</h2>
          <Link href="/organizer/events/new" className="btn-secondary">
            + New event
          </Link>
        </div>
        {events.length === 0 ? (
          <div className="card text-center text-muted">No events yet. Create your first one to start selling.</div>
        ) : (
          <ul className="grid gap-3 sm:grid-cols-2">
            {events.map((e) => (
              <li key={e.id} className="card flex flex-col gap-1">
                <div className="flex items-start justify-between gap-3">
                  <p className="font-semibold">{e.title}</p>
                  <span className={statusBadge(e.status)}>{e.status}</span>
                </div>
                <p className="text-sm text-muted">
                  {e.artist} · {formatEventDateTime(e.startsAt, e.timeZoneId)}
                </p>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}

function statusBadge(status: string): string {
  if (status === "Published") return "badge-success";
  if (status === "Cancelled") return "badge-danger";
  if (status === "Rescheduled") return "badge-warning";
  return "badge-neutral";
}
