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

const fetchMock = vi.fn();

beforeEach(() => {
  auth.mockReset();
  fetchMock.mockReset();
  vi.stubGlobal("fetch", fetchMock);
  auth.mockResolvedValue({ accessToken: "token-123" });
});

describe("holdSeatsAction", () => {
  const fields = { eventId: "ev-1", seatIds: ["s-1", "s-2"], totalAmount: "600000", currency: "VND" };

  it("sends anonymous users to sign in without calling the gateway", async () => {
    auth.mockResolvedValue(null);

    await expect(holdSeatsAction(form(fields))).rejects.toThrow("REDIRECT:/api/auth/signin");
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("does nothing when no seat is selected", async () => {
    await holdSeatsAction(form({ ...fields, seatIds: [] }));

    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("holds the selected seats with the user's token and moves to checkout", async () => {
    fetchMock.mockResolvedValue(
      Response.json({ success: true, holdId: "hold-9", expiresAt: "2026-10-24T12:10:00Z", unavailableSeatIds: [] }),
    );

    const error = (await holdSeatsAction(form(fields)).catch((e: Error) => e)) as Error;

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toMatch(/\/reservation\/holds$/);
    expect(init.headers.Authorization).toBe("Bearer token-123");
    expect(JSON.parse(init.body)).toEqual({ eventId: "ev-1", seatIds: ["s-1", "s-2"] });

    const target = new URL(error.message.replace("REDIRECT:", ""), "http://x");
    expect(target.pathname).toBe("/checkout");
    expect(Object.fromEntries(target.searchParams)).toEqual({
      eventId: "ev-1",
      holdId: "hold-9",
      seatIds: "s-1,s-2",
      expiresAt: "2026-10-24T12:10:00Z",
      totalAmount: "600000",
      currency: "VND",
    });
  });

  it("returns to the event page when another buyer took a seat first", async () => {
    fetchMock.mockResolvedValue(Response.json({ success: false, unavailableSeatIds: ["s-2"] }));

    await expect(holdSeatsAction(form(fields))).rejects.toThrow("REDIRECT:/events/ev-1?holdFailed=1");
  });

  it("fails loudly when the gateway errors", async () => {
    fetchMock.mockResolvedValue(new Response("boom", { status: 503 }));

    await expect(holdSeatsAction(form(fields))).rejects.toThrow("Gateway /reservation/holds responded 503");
  });
});
