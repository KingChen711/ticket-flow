"use client";

import { useMemo, useState } from "react";
import type { PriceTierResponse, SeatAvailabilityResponse } from "@ticketflow/types";
import { holdSeatsAction } from "./actions";
import { useSeatMapLive } from "./useSeatMapLive";

export function SeatPicker({
  eventId,
  seats,
  priceTiers,
  signedIn,
  publicGatewayUrl,
}: {
  eventId: string;
  seats: SeatAvailabilityResponse[];
  priceTiers: PriceTierResponse[];
  signedIn: boolean;
  publicGatewayUrl: string;
}) {
  const [selected, setSelected] = useState<Map<string, string>>(new Map());
  const { revisions, statuses, state } = useSeatMapLive(
    eventId,
    signedIn,
    seats,
    publicGatewayUrl,
  );
  const priceBySection = useMemo(() => new Map(priceTiers.map((tier) => [tier.sectionId, tier.amount])), [priceTiers]);
  const currency = priceTiers[0]?.currency ?? "VND";
  const statusFor = (seat: SeatAvailabilityResponse) => statuses[seat.seatId] ?? seat.status;
  const activeSelected = useMemo(
    () => new Set(
      [...selected]
        .filter(([seatId, revision]) => statuses[seatId] === "Available" && revisions[seatId] === revision)
        .map(([seatId]) => seatId),
    ),
    [revisions, selected, statuses],
  );

  const total = seats
    .filter((seat) => activeSelected.has(seat.seatId))
    .reduce((sum, seat) => sum + (priceBySection.get(seat.sectionId) ?? 0), 0);

  function toggle(seatId: string) {
    setSelected((previous) => {
      const next = new Map(previous);
      if (activeSelected.has(seatId)) next.delete(seatId);
      else if (statuses[seatId] === "Available") next.set(seatId, revisions[seatId] ?? "initial");
      return next;
    });
  }

  if (!signedIn) return <p className="text-sm text-gray-500">Sign in to select seats and buy tickets.</p>;

  return (
    <form action={holdSeatsAction} className="flex flex-col gap-3">
      <input type="hidden" name="eventId" value={eventId} />
      {state !== "live" && (
        <p role="status" className="text-sm text-amber-700">
          {state === "unavailable" ? "Live availability is temporarily unavailable." : "Live updates are reconnecting; snapshot polling is active."}
        </p>
      )}

      <ul className="grid grid-cols-4 gap-2">
        {seats.map((seat) => {
          const available = statusFor(seat) === "Available";
          const isSelected = activeSelected.has(seat.seatId);
          return (
            <li key={seat.seatId}>
              <label className={`flex cursor-pointer flex-col items-center rounded border p-2 text-xs ${
                !available ? "cursor-not-allowed bg-gray-100 text-gray-400" : isSelected
                  ? "border-black bg-black text-white" : "hover:bg-gray-50"
              }`}>
                <input
                  type="checkbox"
                  name="seatIds"
                  value={seat.seatId}
                  disabled={!available}
                  checked={isSelected}
                  onChange={() => toggle(seat.seatId)}
                  className="sr-only"
                />
                {seat.label}
              </label>
            </li>
          );
        })}
      </ul>

      <p className="text-sm">{activeSelected.size} seat(s) selected — {total.toLocaleString()} {currency}</p>
      <button type="submit" disabled={activeSelected.size === 0} className="w-fit rounded bg-black px-4 py-2 text-white disabled:opacity-40">
        Hold seats &amp; continue
      </button>
    </form>
  );
}
