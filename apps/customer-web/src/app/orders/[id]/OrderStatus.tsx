"use client";

import { useEffect, useRef, useState } from "react";
import { fetchJson } from "@ticketflow/http-client";
import {
  parseMyOrderView,
  parseTicketResponses,
  shouldApplyOrderUpdate,
  type MyOrderView,
  type TicketResponse,
} from "@ticketflow/types";

const TERMINAL_STATUSES = new Set(["Confirmed", "Cancelled", "ManualReview", "RefundFailed"]);

export function OrderStatus({ orderId, initialOrder }: { orderId: string; initialOrder: MyOrderView }) {
  const [order, setOrder] = useState(initialOrder);
  const [tickets, setTickets] = useState<TicketResponse[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const latestOrder = useRef(initialOrder);

  useEffect(() => {
    if (TERMINAL_STATUSES.has(initialOrder.status)) return;
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let controller: AbortController | undefined;

    const poll = async () => {
      controller = new AbortController();
      try {
        const incoming = await fetchJson(`/api/orders/${encodeURIComponent(orderId)}`, {
          cache: "no-store",
          signal: controller.signal,
          timeoutMs: 5_000,
          validate: parseMyOrderView,
        });
        if (stopped) return;
        setError(null);
        if (shouldApplyOrderUpdate(latestOrder.current, incoming)) {
          latestOrder.current = incoming;
          setOrder(incoming);
          if (TERMINAL_STATUSES.has(incoming.status)) return;
        }
      } catch (requestError) {
        if (stopped) return;
        setError(requestError instanceof Error ? requestError.message : "Order status is unavailable.");
      }
      timer = setTimeout(poll, 2_000);
    };

    timer = setTimeout(poll, 2_000);
    return () => {
      stopped = true;
      if (timer) clearTimeout(timer);
      controller?.abort();
    };
  }, [initialOrder.status, orderId]);

  useEffect(() => {
    if (order.status !== "Confirmed" || tickets !== null) return;
    const controller = new AbortController();
    void fetchJson(`/api/orders/${encodeURIComponent(orderId)}/tickets`, {
      cache: "no-store",
      signal: controller.signal,
      timeoutMs: 5_000,
      validate: parseTicketResponses,
    }).then((result) => {
      setTickets(result);
      setError(null);
    }).catch((requestError: unknown) => {
      if (!controller.signal.aborted) {
        setError(requestError instanceof Error ? requestError.message : "Tickets are unavailable.");
      }
    });
    return () => controller.abort();
  }, [orderId, order.status, tickets]);

  return (
    <div>
      <p className="text-lg">Status: <span className="font-semibold">{order.status}</span></p>
      {order.status === "Pending" && <p className="text-sm text-gray-500">Processing your order...</p>}
      {order.status === "Cancelled" && <p className="text-sm text-red-600">This order was cancelled.</p>}
      {error && <p role="status" className="mt-2 text-sm text-amber-700">{error} Retrying without overlapping requests.</p>}

      {tickets && (
        <section className="mt-6">
          <h2 className="font-semibold">Your tickets</h2>
          <ul className="mt-2 flex flex-col gap-2">
            {tickets.map((ticket) => (
              <li key={ticket.id} className="flex items-center justify-between rounded border p-3 text-sm">
                <span>Seat {ticket.seatId.slice(0, 8)}</span>
                <a href={`/api/tickets/${ticket.id}/pdf`} target="_blank" rel="noreferrer" className="text-blue-600 underline">View PDF</a>
              </li>
            ))}
          </ul>
        </section>
      )}
    </div>
  );
}
