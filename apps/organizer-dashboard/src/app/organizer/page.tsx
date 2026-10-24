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
      <div className="mx-auto max-w-3xl p-6">
        <p className="text-gray-500">Sign in with an organizer account to manage events.</p>
      </div>
    );
  }

  if (!session.roles?.includes("organizer")) {
    return (
      <div className="mx-auto max-w-3xl p-6">
        <p className="text-red-600">
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

  return (
    <div className="mx-auto max-w-3xl p-6">
      <section className="mb-8 rounded border p-4">
        <h2 className="mb-2 text-lg font-semibold">Revenue</h2>
        {revenue.totalSeats === 0 ? (
          <p className="text-gray-500">No revenue yet.</p>
        ) : revenue.totalRevenue !== null ? (
          <p className="text-2xl font-bold">
            {revenue.totalRevenue.toLocaleString()} {revenue.byCurrency[0]?.currency}{" "}
            <span className="text-sm font-normal">total, {revenue.totalSeats} seats sold</span>
          </p>
        ) : (
          <div>
            <p className="text-lg font-semibold">
              Revenue across {revenue.byCurrency.length} currencies{" "}
              <span className="text-sm font-normal">({revenue.totalSeats} seats sold)</span>
            </p>
            <ul className="mt-2 flex flex-col gap-1 text-sm">
              {revenue.byCurrency.map((currency) => (
                <li key={currency.currency}>
                  {currency.revenue.toLocaleString()} {currency.currency} · {currency.seatCount} seats
                </li>
              ))}
            </ul>
          </div>
        )}
        {revenue.byEvent.length > 0 && (
          <ul className="mt-3 flex flex-col gap-1 text-sm">
            {revenue.byEvent.map((r) => (
              <li key={`${r.eventId}:${r.currency}`} className="flex justify-between">
                <span>{titleByEventId.get(r.eventId) ?? r.eventId}</span>
                <span>
                  {r.revenue.toLocaleString()} {r.currency} · {r.seatCount} seats
                </span>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section>
        <div className="mb-2 flex items-center justify-between">
          <h2 className="text-lg font-semibold">Events</h2>
          <Link href="/organizer/events/new" className="text-sm text-blue-600 underline">
            + New event
          </Link>
        </div>
        {events.length === 0 ? (
          <p className="text-gray-500">No events yet.</p>
        ) : (
          <ul className="flex flex-col gap-2">
            {events.map((e) => (
              <li key={e.id} className="rounded border p-3 text-sm">
                <p className="font-medium">{e.title}</p>
                <p className="text-gray-500">
                  {e.artist} · {formatEventDateTime(e.startsAt, e.timeZoneId)} · {e.status}
                </p>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}
