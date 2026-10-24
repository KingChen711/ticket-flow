import { beforeEach, describe, expect, it, vi } from "vitest";

const auth = vi.fn();
vi.mock("@/auth", () => ({ auth: () => auth() }));
// next/navigation's redirect throws to stop the action; mirror that so code after it never runs.
vi.mock("next/navigation", () => ({
  redirect: (url: string) => {
    throw new Error(`REDIRECT:${url}`);
  },
}));

const { holdSeatsAction } = await import("./actions");

function form(fields: Record<string, string | string[]>): FormData {
  const data = new FormData();
  for (const [key, value] of Object.entries(fields))
    for (const v of Array.isArray(value) ? value : [value]) data.append(key, v);
  return data;
}

const quote = {
  holdId: "hold-9",
  eventId: "ev-1",
  ownerUserId: "user-1",
  seatIds: ["s-1", "s-2"],
  amount: 600000,
  currency: "VND",
  priceSnapshotVersion: 3,
  generation: 7,
  expiresAt: "2026-10-24T12:10:00Z",
};

function holdResult(overrides: Record<string, unknown> = {}) {
  return {
    success: true,
    holdId: "hold-9",
    expiresAt: "2026-10-24T12:10:00Z",
    unavailableSeatIds: [],
    quote,
    reason: "None",
    reasonDetail: null,
    ...overrides,
  };
}

const fetchMock = vi.fn();
const redirectOf = (action: Promise<unknown>) => action.then(() => null, (e: Error) => e.message.replace("REDIRECT:", ""));

beforeEach(() => {
  auth.mockReset();
  fetchMock.mockReset();
  vi.stubGlobal("fetch", fetchMock);
  auth.mockResolvedValue({ serverAccessToken: "token-123" });
});

describe("holdSeatsAction", () => {
  const fields = { eventId: "ev-1", seatIds: ["s-1", "s-2"] };

  it("sends anonymous users to sign in without calling the gateway", async () => {
    auth.mockResolvedValue(null);

    expect(await redirectOf(holdSeatsAction(form(fields)))).toBe("/api/auth/signin");
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("sends users whose refresh token failed back to sign in", async () => {
    auth.mockResolvedValue({ serverAccessToken: "stale", error: "RefreshTokenError" });

    expect(await redirectOf(holdSeatsAction(form(fields)))).toBe("/api/auth/signin");
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("does nothing when no seat is selected", async () => {
    await holdSeatsAction(form({ ...fields, seatIds: [] }));

    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("holds only the selected seats and moves to checkout with the hold, never a client price", async () => {
    fetchMock.mockResolvedValue(Response.json(holdResult()));

    const target = new URL((await redirectOf(holdSeatsAction(form({ ...fields, totalAmount: "1" }))))!, "http://x");

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toMatch(/\/reservation\/holds$/);
    expect(init.method).toBe("POST");
    expect(init.headers.Authorization).toBe("Bearer token-123");
    expect(JSON.parse(init.body)).toEqual({ eventId: "ev-1", seatIds: ["s-1", "s-2"] });

    expect(target.pathname).toBe("/checkout");
    expect([...target.searchParams.keys()].sort()).toEqual(["holdId", "idempotencyKey"]);
    expect(target.searchParams.get("holdId")).toBe("hold-9");
    expect(target.searchParams.get("idempotencyKey")).toMatch(/^[0-9a-f-]{36}$/);
  });

  it("returns to the event page with a conflict when another buyer took a seat first", async () => {
    fetchMock.mockResolvedValue(
      Response.json(holdResult({ success: false, holdId: null, expiresAt: null, quote: null, reason: "SeatsUnavailable", unavailableSeatIds: ["s-2"] })),
    );

    expect(await redirectOf(holdSeatsAction(form(fields)))).toBe("/events/ev-1?holdFailed=conflict");
  });

  it("reads a rate-limit verdict from the gateway's error body", async () => {
    fetchMock.mockResolvedValue(
      Response.json(holdResult({ success: false, holdId: null, expiresAt: null, quote: null, reason: "RateLimited" }), { status: 429 }),
    );

    expect(await redirectOf(holdSeatsAction(form(fields)))).toBe("/events/ev-1?holdFailed=rate-limited");
  });

  it("degrades to 'unavailable' when the gateway fails without a hold verdict", async () => {
    fetchMock.mockResolvedValue(new Response("boom", { status: 503 }));

    expect(await redirectOf(holdSeatsAction(form(fields)))).toBe("/events/ev-1?holdFailed=unavailable");
  });
});
