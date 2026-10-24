import { describe, expect, it } from "vitest";
import type { PriceTierResponse, SeatAvailabilityResponse } from "@ticketflow/types";
import { applySeatsUpdated, effectiveStatus, selectionTotal } from "./seatSelection";

const seat = (seatId: string, sectionId: string, status = "Available"): SeatAvailabilityResponse =>
  ({ seatId, sectionId, label: seatId, status }) as SeatAvailabilityResponse;
const tier = (sectionId: string, amount: number): PriceTierResponse =>
  ({ id: `tier-${sectionId}`, sectionId, amount, currency: "VND" }) as PriceTierResponse;

describe("selectionTotal", () => {
  const seats = [seat("vip-1", "vip"), seat("std-1", "std"), seat("std-2", "std")];
  const tiers = [tier("vip", 1_000_000), tier("std", 300_000)];

  it("sums each selected seat at its own section price", () => {
    expect(selectionTotal(seats, tiers, new Set(["vip-1", "std-2"]))).toBe(1_300_000);
  });

  it("is zero with nothing selected", () => {
    expect(selectionTotal(seats, tiers, new Set())).toBe(0);
  });

  it("counts a seat in a section without a price tier as zero", () => {
    expect(selectionTotal([seat("x-1", "unpriced")], tiers, new Set(["x-1"]))).toBe(0);
  });
});

describe("live seat status", () => {
  it("prefers the live status over the status the page was rendered with", () => {
    expect(effectiveStatus(seat("a", "s", "Available"), { a: "Held" })).toBe("Held");
    expect(effectiveStatus(seat("b", "s", "Sold"), { a: "Held" })).toBe("Sold");
  });

  it("applies a seatsUpdated broadcast without touching other seats or the previous map", () => {
    const prev = { a: "Held", c: "Sold" };

    const next = applySeatsUpdated(prev, { seatIds: ["a", "b"], status: "Available" });

    expect(next).toEqual({ a: "Available", b: "Available", c: "Sold" });
    expect(prev).toEqual({ a: "Held", c: "Sold" });
  });
});
