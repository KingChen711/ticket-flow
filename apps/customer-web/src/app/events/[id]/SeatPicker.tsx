"use client";

import { useState } from "react";
import type { PriceTierResponse, SeatAvailabilityResponse } from "@ticketflow/types";
import { holdSeatsAction } from "./actions";

export function SeatPicker({
  eventId,
  seats,
  priceTiers,
  currency,
  signedIn,
}: {
  eventId: string;
  seats: SeatAvailabilityResponse[];
  priceTiers: PriceTierResponse[];
  currency: string;
  signedIn: boolean;
}) {
  const [selected, setSelected] = useState<Set<string>>(new Set());

  const priceBySection = new Map(priceTiers.map((t) => [t.sectionId, t.amount]));
  const priceFor = (seat: SeatAvailabilityResponse) => priceBySection.get(seat.sectionId) ?? 0;
  const total = seats
    .filter((s) => selected.has(s.seatId))
    .reduce((sum, s) => sum + priceFor(s), 0);

  function toggle(seatId: string) {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(seatId)) next.delete(seatId);
      else next.add(seatId);
      return next;
    });
  }

  if (!signedIn) {
    return <p className="text-sm text-gray-500">Sign in to select seats and buy tickets.</p>;
  }

  return (
    <form action={holdSeatsAction} className="flex flex-col gap-3">
      <input type="hidden" name="eventId" value={eventId} />
      <input type="hidden" name="totalAmount" value={total} />
      <input type="hidden" name="currency" value={currency} />

      <ul className="grid grid-cols-4 gap-2">
        {seats.map((seat) => {
          const available = seat.status === "Available";
          const isSelected = selected.has(seat.seatId);
          return (
            <li key={seat.seatId}>
              <label
                className={`flex cursor-pointer flex-col items-center rounded border p-2 text-xs ${
                  !available
                    ? "cursor-not-allowed bg-gray-100 text-gray-400"
                    : isSelected
                      ? "border-black bg-black text-white"
                      : "hover:bg-gray-50"
                }`}
              >
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

      <p className="text-sm">
        {selected.size} seat(s) selected — {total.toLocaleString()} {currency}
      </p>

      <button
        type="submit"
        disabled={selected.size === 0}
        className="w-fit rounded bg-black px-4 py-2 text-white disabled:opacity-40"
      >
        Hold seats &amp; continue
      </button>
    </form>
  );
}
