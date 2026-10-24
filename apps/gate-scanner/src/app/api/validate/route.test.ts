import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ValidateTicketResponse } from "@ticketflow/types";

const auth = vi.fn();
vi.mock("@/auth", () => ({ auth: () => auth() }));

const { POST } = await import("./route");

const fetchMock = vi.fn();
const scan = (body: unknown) => new Request("http://localhost/api/validate", { method: "POST", body: JSON.stringify(body) });

beforeEach(() => {
  auth.mockReset();
  fetchMock.mockReset();
  vi.stubGlobal("fetch", fetchMock);
});

describe("POST /api/validate", () => {
  it("rejects a scanner that is not signed in, without calling the gateway", async () => {
    auth.mockResolvedValue(null);

    const res = await POST(scan({ qrPayload: "ticket-1" }));

    expect(res.status).toBe(401);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("forwards only the QR payload with the session token and returns the verdict", async () => {
    auth.mockResolvedValue({ accessToken: "org-token" });
    const verdict: ValidateTicketResponse = { status: "Valid", ticketId: "t-1", checkedInAt: "2026-12-01T19:05:00Z" };
    fetchMock.mockResolvedValue(Response.json(verdict));

    const res = await POST(scan({ qrPayload: "ticket-1", injected: "ignored" }));

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toMatch(/\/ticketing\/tickets\/validate$/);
    expect(init.method).toBe("POST");
    expect(init.headers["Content-Type"]).toBe("application/json");
    expect(init.headers.Authorization).toBe("Bearer org-token");
    expect(JSON.parse(init.body)).toEqual({ qrPayload: "ticket-1" });
    expect(res.status).toBe(200);
    expect(await res.json()).toEqual(verdict);
  });

  it("passes a gateway error status through to the scanner", async () => {
    auth.mockResolvedValue({ accessToken: "customer-token" });
    fetchMock.mockResolvedValue(new Response(null, { status: 403 }));

    const res = await POST(scan({ qrPayload: "ticket-1" }));

    expect(res.status).toBe(403);
  });
});
