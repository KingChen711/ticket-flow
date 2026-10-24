import assert from "node:assert/strict";
import test from "node:test";
import {
  formatEventDateTime,
  isNewerSeatVersion,
  parseMyOrderView,
  parseRevenueResult,
  parseSearchEventsResult,
  serializeLocalDateTime,
  shouldApplyOrderUpdate,
} from "./index.ts";

const orderPayload = {
  id: "order-1",
  customerId: "customer-1",
  catalogEventId: "event-1",
  seatIds: ["seat-1"],
  totalAmount: 500_000,
  currency: "VND",
  status: "Pending",
  placedAt: "2026-07-26T12:00:00Z",
  confirmedAt: null,
  cancelledAt: null,
  ticketPdfUrls: null,
  sequence: 4,
};

test("runtime schema rejects a drifted search payload", () => {
  assert.throws(() => parseSearchEventsResult({ items: [{ eventId: "only-an-id" }], total: 1 }));
});

test("revenue schema accepts an empty result with a nullable total", () => {
  assert.deepEqual(parseRevenueResult({
    totalRevenue: null,
    totalSeats: 0,
    byEvent: [],
    byCurrency: [],
  }), {
    totalRevenue: null,
    totalSeats: 0,
    byEvent: [],
    byCurrency: [],
  });
});

test("revenue schema preserves currency breakdowns instead of adding unlike currencies", () => {
  const result = parseRevenueResult({
    totalRevenue: null,
    totalSeats: 3,
    byEvent: [
      { eventId: "event-vnd", currency: "VND", revenue: 500_000, seatCount: 2 },
      { eventId: "event-usd", currency: "USD", revenue: 20, seatCount: 1 },
    ],
    byCurrency: [
      { currency: "VND", revenue: 500_000, seatCount: 2 },
      { currency: "USD", revenue: 20, seatCount: 1 },
    ],
  });
  assert.equal(result.totalRevenue, null);
  assert.deepEqual(result.byCurrency.map(({ currency }) => currency), ["VND", "USD"]);
});

test("order polling ignores a stale sequence", () => {
  const current = parseMyOrderView(orderPayload);
  const stale = parseMyOrderView({ ...orderPayload, status: "SeatsConfirmed", sequence: 3 });
  const duplicate = parseMyOrderView({ ...orderPayload, status: "Cancelled", sequence: 4 });
  const newer = parseMyOrderView({ ...orderPayload, status: "Confirmed", sequence: 5 });
  assert.equal(shouldApplyOrderUpdate(current, stale), false);
  assert.equal(shouldApplyOrderUpdate(current, duplicate), false);
  assert.equal(shouldApplyOrderUpdate(current, newer), true);
});

test("seat updates compare allocation generation then occurrence time", () => {
  const current = { generation: 8, updatedAt: "2026-07-26T12:00:00Z" };
  assert.equal(isNewerSeatVersion({ generation: 7, updatedAt: "2026-07-26T13:00:00Z" }, current), false);
  assert.equal(isNewerSeatVersion({ generation: 8, updatedAt: "2026-07-26T12:00:00Z" }, current), false);
  assert.equal(isNewerSeatVersion({ generation: 8, updatedAt: "2026-07-26T12:00:01Z" }, current), true);
  assert.equal(isNewerSeatVersion({ generation: 9, updatedAt: "2026-07-26T11:00:00Z" }, current), true);
});

test("datetime-local serialization sends UTC and the browser offset", () => {
  const local = "2026-07-26T19:30";
  const result = serializeLocalDateTime(local);
  const parsed = new Date(local);
  assert.equal(result.utc, parsed.toISOString());
  assert.equal(result.utcOffsetMinutes, -parsed.getTimezoneOffset());
  assert.match(formatEventDateTime(result.utc, "Asia/Saigon"), /GMT\+7/);
});
