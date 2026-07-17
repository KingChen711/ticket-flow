import Link from "next/link";
import type { EventResponse, RevenueResult } from "@ticketflow/types";
import { auth } from "@/auth";
import { fetchGatewayAuthed } from "@/lib/gateway";

// Role gate lives here, not just at the backend (docs/phase-8-plan.md Commit 7 — "customer login
// vào app này phải bị chặn ở UI lẫn API"). GET /analytics/revenue already 403s a non-organizer
// token at the backend (RequireOrganizer, Phase 7 Commit 2); this page also refuses to render the
// dashboard for a signed-in customer instead of letting that 403 surface as a raw fetch error.
export default async function DashboardPage() {
  const session = await auth();

  if (!session) {
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
    fetchGatewayAuthed<EventResponse[]>("/catalog/events", session.accessToken!),
    fetchGatewayAuthed<RevenueResult>("/analytics/revenue", session.accessToken!),
  ]);

  const titleByEventId = new Map(events.map((e) => [e.id, e.title]));

  return (
    <div className="mx-auto max-w-3xl p-6">
      <section className="mb-8 rounded border p-4">
        <h2 className="mb-2 text-lg font-semibold">Revenue</h2>
        <p className="text-2xl font-bold">
          {revenue.totalRevenue.toLocaleString()}{" "}
          <span className="text-sm font-normal">total, {revenue.totalSeats} seats sold</span>
        </p>
        {revenue.byEvent.length > 0 && (
          <ul className="mt-3 flex flex-col gap-1 text-sm">
            {revenue.byEvent.map((r) => (
              <li key={r.eventId} className="flex justify-between">
                <span>{titleByEventId.get(r.eventId) ?? r.eventId}</span>
                <span>
                  {r.revenue.toLocaleString()} · {r.seatCount} seats
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
                  {e.artist} · {new Date(e.startsAt).toLocaleString()} · {e.status}
                </p>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}
