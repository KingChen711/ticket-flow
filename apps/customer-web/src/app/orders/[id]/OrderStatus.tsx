"use client";

import { useEffect, useState } from "react";
import type { MyOrderView, TicketResponse } from "@ticketflow/types";

const TERMINAL_STATUSES = new Set(["Confirmed", "Cancelled"]);

// The saga (Phase 5) drives Pending -> Confirmed asynchronously (hold confirm -> payment ->
// tickets issued -> order completed) — polling is the simplest way for the UI to catch up, no
// WebSocket/SSE for this one-shot status change (SignalR is reserved for the seat map, Commit 6).
export function OrderStatus({ orderId, initialOrder }: { orderId: string; initialOrder: MyOrderView }) {
  const [order, setOrder] = useState(initialOrder);
  const [tickets, setTickets] = useState<TicketResponse[] | null>(null);

  useEffect(() => {
    if (TERMINAL_STATUSES.has(order.status)) return;

    const interval = setInterval(async () => {
      const res = await fetch(`/api/orders/${orderId}`, { cache: "no-store" });
      if (res.ok) setOrder(await res.json());
    }, 2000);

    return () => clearInterval(interval);
  }, [orderId, order.status]);

  useEffect(() => {
    if (order.status !== "Confirmed" || tickets !== null) return;

    fetch(`/api/orders/${orderId}/tickets`, { cache: "no-store" })
      .then((res) => res.json())
      .then(setTickets);
  }, [orderId, order.status, tickets]);

  return (
    <div>
      <p className="text-lg">
        Status: <span className="font-semibold">{order.status}</span>
      </p>
      {order.status === "Pending" && <p className="text-sm text-gray-500">Processing your order...</p>}
      {order.status === "Cancelled" && <p className="text-sm text-red-600">This order was cancelled.</p>}

      {tickets && (
        <section className="mt-6">
          <h2 className="font-semibold">Your tickets</h2>
          <ul className="mt-2 flex flex-col gap-2">
            {tickets.map((ticket) => (
              <li key={ticket.id} className="flex items-center justify-between rounded border p-3 text-sm">
                <span>Seat {ticket.seatId.slice(0, 8)}</span>
                <a
                  href={`/api/tickets/${ticket.id}/pdf`}
                  target="_blank"
                  rel="noreferrer"
                  className="text-blue-600 underline"
                >
                  View PDF
                </a>
              </li>
            ))}
          </ul>
        </section>
      )}
    </div>
  );
}
