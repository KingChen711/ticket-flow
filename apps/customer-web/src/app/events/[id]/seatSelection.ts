import type { PriceTierResponse, SeatAvailabilityResponse } from "@ticketflow/types";

export interface SeatsUpdatedPayload {
  seatIds: string[];
  status: string;
}

// A live SignalR status (Held/Sold/Available) wins over the status the page was rendered with.
export function effectiveStatus(seat: SeatAvailabilityResponse, liveStatuses: Record<string, string>): string {
  return liveStatuses[seat.seatId] ?? seat.status;
}

// Display total only: the order total that is charged is priced again on the server.
export function selectionTotal(
  seats: SeatAvailabilityResponse[],
  priceTiers: PriceTierResponse[],
  selected: ReadonlySet<string>,
): number {
  const priceBySection = new Map(priceTiers.map((t) => [t.sectionId, t.amount]));
  return seats
    .filter((s) => selected.has(s.seatId))
    .reduce((sum, s) => sum + (priceBySection.get(s.sectionId) ?? 0), 0);
}

export function applySeatsUpdated(
  prev: Record<string, string>,
  payload: SeatsUpdatedPayload,
): Record<string, string> {
  const next = { ...prev };
  for (const seatId of payload.seatIds) next[seatId] = payload.status;
  return next;
}
