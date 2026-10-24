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

  const sections = useMemo(() => {
    const order = new Map(priceTiers.map((tier, index) => [tier.sectionId, index]));
    const groups = new Map<string, SeatAvailabilityResponse[]>();
    for (const seat of seats) groups.set(seat.sectionId, [...(groups.get(seat.sectionId) ?? []), seat]);
    return [...groups.entries()]
      .sort(([a], [b]) => (order.get(a) ?? Number.MAX_SAFE_INTEGER) - (order.get(b) ?? Number.MAX_SAFE_INTEGER))
      .map(([sectionId, sectionSeats]) => ({
        sectionId,
        name: order.has(sectionId) ? `Section ${order.get(sectionId)! + 1}` : "Section",
        price: priceBySection.get(sectionId),
        seats: sectionSeats,
      }));
  }, [priceBySection, priceTiers, seats]);

  if (!signedIn) {
    return (
      <p className="alert-info">Sign in to select seats and buy tickets.</p>
    );
  }

  return (
    <form action={holdSeatsAction} className="flex flex-col gap-5">
      <input type="hidden" name="eventId" value={eventId} />
      {state !== "live" && (
        <p role="status" className="alert-warning">
          {state === "unavailable" ? "Live availability is temporarily unavailable." : "Live updates are reconnecting; snapshot polling is active."}
        </p>
      )}

      <SeatLegend />

      {sections.map((section) => (
        <fieldset key={section.sectionId} className="flex flex-col gap-2">
          <legend className="mb-2 flex w-full items-baseline justify-between text-sm">
            <span className="font-semibold">{section.name}</span>
            {section.price !== undefined && (
              <span className="text-muted">{section.price.toLocaleString()} {currency} / seat</span>
            )}
          </legend>
          <ul className="grid grid-cols-[repeat(auto-fill,minmax(3.25rem,1fr))] gap-2">
            {section.seats.map((seat) => {
              const status = statusFor(seat);
              const available = status === "Available";
              const isSelected = activeSelected.has(seat.seatId);
              return (
                <li key={seat.seatId}>
                  <label
                    title={`${seat.label} · ${isSelected ? "Selected" : status}`}
                    className={`relative flex min-h-11 select-none items-center justify-center rounded-md border text-xs font-semibold transition-colors duration-150 has-[:focus-visible]:outline-2 has-[:focus-visible]:outline-offset-2 has-[:focus-visible]:outline-ring ${
                      isSelected
                        ? "cursor-pointer border-primary bg-primary text-on-primary"
                        : available
                          ? "cursor-pointer border-border-strong bg-surface hover:border-primary hover:bg-primary-soft"
                          : status === "Held"
                            ? "cursor-not-allowed border-dashed border-warning/60 bg-warning-soft text-warning"
                            : "cursor-not-allowed border-border bg-surface-muted text-muted line-through"
                    }`}
                  >
                    <input
                      type="checkbox"
                      name="seatIds"
                      value={seat.seatId}
                      disabled={!available}
                      checked={isSelected}
                      onChange={() => toggle(seat.seatId)}
                      aria-label={`Seat ${seat.label}, ${isSelected ? "selected" : status.toLowerCase()}`}
                      className="peer sr-only scroll-mb-28"
                    />
                    {isSelected && <CheckIcon />}
                    {seat.label}
                  </label>
                </li>
              );
            })}
          </ul>
        </fieldset>
      ))}

      <div className="sticky bottom-0 -mx-5 -mb-5 flex flex-wrap items-center justify-between gap-3 rounded-b-xl border-t border-border bg-surface/95 px-5 py-4 backdrop-blur">
        <p className="text-sm" aria-live="polite">
          <span className="font-semibold">{activeSelected.size} seat(s) selected</span>
          <span className="text-muted"> · </span>
          <span className="font-semibold">{total.toLocaleString()} {currency}</span>
        </p>
        <button type="submit" disabled={activeSelected.size === 0} className="btn-primary">
          Hold seats &amp; continue
        </button>
      </div>
    </form>
  );
}

function SeatLegend() {
  const items = [
    { label: "Available", swatch: "border-border-strong bg-surface" },
    { label: "Selected", swatch: "border-primary bg-primary" },
    { label: "Held by someone", swatch: "border-dashed border-warning/60 bg-warning-soft" },
    { label: "Sold", swatch: "border-border bg-surface-muted", mark: true },
  ];
  return (
    <ul aria-label="Seat legend" className="flex flex-wrap gap-x-4 gap-y-2 text-xs text-muted">
      {items.map((item) => (
        <li key={item.label} className="flex items-center gap-1.5">
          <span aria-hidden="true" className={`relative inline-block size-4 rounded border ${item.swatch}`}>
            {item.mark && <span className="absolute inset-x-0.5 top-1/2 h-px -translate-y-1/2 bg-muted" />}
          </span>
          {item.label}
        </li>
      ))}
    </ul>
  );
}

function CheckIcon() {
  return (
    <svg aria-hidden="true" viewBox="0 0 24 24" className="mr-0.5 size-3" fill="none" stroke="currentColor" strokeWidth={3} strokeLinecap="round" strokeLinejoin="round">
      <path d="m5 12 5 5 9-10" />
    </svg>
  );
}
