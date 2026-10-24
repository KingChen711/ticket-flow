import { beforeEach, describe, expect, it, vi } from "vitest";

const auth = vi.fn();
vi.mock("@/auth", () => ({ auth: () => auth() }));
vi.mock("next/navigation", () => ({
  redirect: (url: string) => {
    throw new Error(`REDIRECT:${url}`);
  },
}));

const { createEventAction } = await import("./actions");

const fetchMock = vi.fn();
const calls = () =>
  fetchMock.mock.calls.map(([url, init]) => ({
    path: new URL(url).pathname,
    method: init?.method ?? "GET",
    authorization: init?.headers?.Authorization,
    body: init?.body ? JSON.parse(init.body) : undefined,
  }));
const initial = { workflow: null, error: null };

function workflow(overrides: Record<string, unknown> = {}) {
  return {
    workflowId: "wf-1",
    creationKey: "key-1",
    venueId: "venue-1",
    eventId: "ev-1",
    status: "Running",
    lastCompletedStep: "SeatMapCreated",
    failedStep: null,
    lastError: null,
    canResume: true,
    canCompensate: false,
    updatedAt: "2026-10-24T12:00:00Z",
    ...overrides,
  };
}

function form(extra: [string, string][] = []): FormData {
  const data = new FormData();
  const fields: [string, string][] = [
    ["creationKey", "key-1"],
    ["venueMode", "existing"],
    ["venueId", "venue-1"],
    ["title", "Concert"],
    ["description", "Desc"],
    ["artist", "Band"],
    ["startsAtUtc", "2026-12-01T12:00:00.000Z"],
    ["endsAtUtc", "2026-12-01T15:00:00.000Z"],
    ["timeZoneId", "Asia/Ho_Chi_Minh"],
    ["startUtcOffsetMinutes", "420"],
    ["endUtcOffsetMinutes", "420"],
    ["sectionName", "VIP"],
    ["sectionCapacity", "2"],
    ["sectionPrice", "1000000"],
    ["sectionName", "STD"],
    ["sectionCapacity", "3"],
    ["sectionPrice", "300000"],
    ...extra,
  ];
  for (const [k, v] of fields) data.append(k, v);
  return data;
}

beforeEach(() => {
  auth.mockReset();
  fetchMock.mockReset();
  vi.stubGlobal("fetch", fetchMock);
  auth.mockResolvedValue({ serverAccessToken: "t", roles: ["organizer"] });
});

describe("createEventAction", () => {
  it("refuses a signed-in user who is not an organizer, without calling the gateway", async () => {
    auth.mockResolvedValue({ serverAccessToken: "t", roles: ["customer"] });

    expect(await createEventAction(initial, form())).toEqual({ workflow: null, error: "Only organizers can create events." });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("starts one server-side onboarding workflow carrying the whole event, then lands on the dashboard", async () => {
    fetchMock.mockResolvedValue(Response.json(workflow({ status: "Completed", lastCompletedStep: "Published", canResume: false })));

    await expect(createEventAction(initial, form())).rejects.toThrow("REDIRECT:/organizer");

    const [call] = calls();
    expect(calls()).toHaveLength(1);
    expect(call).toMatchObject({ path: "/catalog/workflows/event-onboarding", method: "POST", authorization: "Bearer t" });
    expect(call.body).toEqual({
      creationKey: "key-1",
      existingVenueId: "venue-1",
      newVenue: null,
      title: "Concert",
      description: "Desc",
      artist: "Band",
      startsAt: "2026-12-01T12:00:00.000Z",
      endsAt: "2026-12-01T15:00:00.000Z",
      timeZoneId: "Asia/Ho_Chi_Minh",
      startUtcOffsetMinutes: 420,
      endUtcOffsetMinutes: 420,
      sections: [
        { name: "VIP", capacity: 2, price: 1_000_000, currency: "VND" },
        { name: "STD", capacity: 3, price: 300_000, currency: "VND" },
      ],
    });
  });

  it("returns an unfinished workflow so the form can offer to resume it", async () => {
    const failed = workflow({ status: "Failed", failedStep: "InventoryProvisioned", lastError: "reservation down" });
    fetchMock.mockResolvedValue(Response.json(failed));

    expect(await createEventAction(initial, form())).toEqual({ workflow: failed, error: "reservation down" });
  });

  it("resumes an existing workflow instead of starting a second event", async () => {
    fetchMock.mockResolvedValue(Response.json(workflow({ status: "Completed", canResume: false })));

    await expect(createEventAction(initial, form([["workflowId", "wf-1"]]))).rejects.toThrow("REDIRECT:/organizer");

    expect(calls().map((c) => [c.method, c.path])).toEqual([["POST", "/catalog/workflows/event-onboarding/wf-1/resume"]]);
  });

  it("recovers the workflow by creation key when the start request fails mid-flight", async () => {
    const running = workflow();
    fetchMock.mockImplementation(async (url: string) =>
      new URL(url).pathname.startsWith("/catalog/workflows/event-onboarding/by-key/")
        ? Response.json(running)
        : new Response(null, { status: 504 }),
    );

    const state = await createEventAction(initial, form());

    expect(calls().map((c) => [c.method, c.path])).toEqual([
      ["POST", "/catalog/workflows/event-onboarding"],
      ["GET", "/catalog/workflows/event-onboarding/by-key/key-1"],
    ]);
    expect(state.workflow).toEqual(running);
    expect(state.error).toBeTruthy();
  });

  it("refuses to submit when the browser did not supply timezone data", async () => {
    const data = form();
    data.delete("timeZoneId");

    expect((await createEventAction(initial, data)).error).toMatch(/timezone/i);
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
