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
import { orderBadge, statusLabel } from "@/lib/status";

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
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let controller: AbortController | undefined;

    const loadTickets = async () => {
      controller = new AbortController();
      try {
        const result = await fetchJson(`/api/orders/${encodeURIComponent(orderId)}/tickets`, {
          cache: "no-store",
          signal: controller.signal,
          timeoutMs: 5_000,
          validate: parseTicketResponses,
        });
        if (stopped) return;
        setTickets(result);
        setError(null);
      } catch (requestError) {
        if (stopped || controller.signal.aborted) return;
        setError(requestError instanceof Error ? requestError.message : "Tickets are unavailable.");
        timer = setTimeout(loadTickets, 2_000);
      }
    };

    void loadTickets();
    return () => {
      stopped = true;
      if (timer) clearTimeout(timer);
      controller?.abort();
    };
  }, [orderId, order.status, tickets]);

  // Forward milestones of the order (Ordering's OrderStatus enum); compensation states are not
  // progress, so the bar is hidden for them.
  const steps = ["Pending", "SeatsConfirmed", "PaymentAuthorized", "TicketsIssued"];
  const reached = order.status === "Confirmed" ? steps.length - 1 : steps.indexOf(order.status);

  return (
    <div className="flex flex-col gap-4">
      <p className="flex items-center gap-2 text-lg" aria-live="polite">
        Status: <span className={`${orderBadge(order.status)} text-sm`}>{statusLabel(order.status)}</span>
      </p>

      {reached >= 0 && (
        <ol aria-label="Order progress" className="grid grid-cols-4 gap-2 text-xs">
          {["Placed", "Seats", "Payment", "Tickets"].map((label, index) => (
            <li key={label} className="flex flex-col gap-1.5">
              <span aria-hidden="true" className={`h-1.5 rounded-full ${index <= reached ? "bg-primary" : "bg-surface-muted"}`} />
              <span className={index <= reached ? "font-medium" : "text-muted"}>
                {label}
                <span className="sr-only">{index <= reached ? " (done)" : " (pending)"}</span>
              </span>
            </li>
          ))}
        </ol>
      )}

      {order.status !== "Confirmed" && reached >= 0 && (
        <p className="text-sm text-muted">Processing your order... this page updates by itself.</p>
      )}
      {order.status === "Cancelled" && <p className="alert-danger">This order was cancelled. Any payment is refunded and the seats are released.</p>}
      {order.status === "ManualReview" && <p className="alert-warning">This order needs a manual check by our team. You do not need to pay again.</p>}
      {error && <p role="status" className="alert-warning">{error} Retrying without overlapping requests.</p>}

      {tickets && (
        <section aria-labelledby="tickets-heading" className="border-t border-border pt-4">
          <h2 id="tickets-heading" className="font-semibold">Your tickets</h2>
          <ul className="mt-3 flex flex-col gap-2">
            {tickets.map((ticket) => (
              <li key={ticket.id} className="flex items-center justify-between gap-3 rounded-lg border border-border p-3 text-sm">
                <span>Seat {ticket.seatId.slice(0, 8)}</span>
                <a href={`/api/tickets/${ticket.id}/pdf`} target="_blank" rel="noreferrer" className="btn-secondary">View PDF</a>
              </li>
            ))}
          </ul>
        </section>
      )}
    </div>
  );
}
