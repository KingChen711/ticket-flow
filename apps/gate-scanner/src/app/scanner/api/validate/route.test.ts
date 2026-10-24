import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ValidateTicketResponse } from "@ticketflow/types";

const auth = vi.fn();
vi.mock("@/auth", () => ({ auth: () => auth() }));

const { POST } = await import("./route");

const EVENT_ID = "3f2c1a8e-6b1d-4c2e-9a7f-1b2c3d4e5f60";
const fetchMock = vi.fn();
const scan = (body: unknown) =>
  new Request("http://localhost/scanner/api/validate", { method: "POST", body: typeof body === "string" ? body : JSON.stringify(body) });
const validScan = { qrPayload: "ticket-1", eventId: EVENT_ID, gateId: "gate-A" };

beforeEach(() => {
  auth.mockReset();
  fetchMock.mockReset();
  vi.stubGlobal("fetch", fetchMock);
  auth.mockResolvedValue({ serverAccessToken: "org-token" });
});

describe("POST /scanner/api/validate", () => {
  it("rejects a scanner that is not signed in, without calling the gateway", async () => {
    auth.mockResolvedValue(null);

    const res = await POST(scan(validScan));

    expect(res.status).toBe(401);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("asks for reauthentication when the session refresh failed", async () => {
    auth.mockResolvedValue({ serverAccessToken: "stale", error: "RefreshTokenError" });

    expect((await POST(scan(validScan))).status).toBe(401);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it.each([
    ["malformed JSON", "{"],
    ["a missing gate", { qrPayload: "ticket-1", eventId: EVENT_ID }],
    ["an event id that is not a UUID", { ...validScan, eventId: "ev-1" }],
    ["a blank gate", { ...validScan, gateId: "   " }],
  ])("rejects %s with 400 before reaching the gateway", async (_, body) => {
    expect((await POST(scan(body))).status).toBe(400);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("forwards only the QR payload, event and gate with the session token and returns the verdict", async () => {
    const verdict: ValidateTicketResponse = { status: "Valid", ticketId: "t-1", checkedInAt: "2026-12-01T19:05:00.000Z" };
    fetchMock.mockResolvedValue(Response.json(verdict));

    const res = await POST(scan({ ...validScan, injected: "ignored" }));

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toMatch(/\/ticketing\/tickets\/validate$/);
    expect(init.method).toBe("POST");
    expect(init.headers["Content-Type"]).toBe("application/json");
    expect(init.headers.Authorization).toBe("Bearer org-token");
    expect(JSON.parse(init.body)).toEqual(validScan);
    expect(res.status).toBe(200);
    expect(await res.json()).toEqual(verdict);
  });

  it("passes a gateway error status through to the scanner", async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 403 }));

    expect((await POST(scan(validScan))).status).toBe(403);
  });
});
