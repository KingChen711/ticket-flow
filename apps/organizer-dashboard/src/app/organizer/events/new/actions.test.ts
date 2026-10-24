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
const calls = () => fetchMock.mock.calls.map(([url, init]) => ({ path: new URL(url).pathname, body: JSON.parse(init.body) }));

function form(): FormData {
  const data = new FormData();
  const fields: [string, string][] = [
    ["venueMode", "existing"],
    ["venueId", "venue-1"],
    ["title", "Concert"],
    ["description", "Desc"],
    ["artist", "Band"],
    ["startsAt", "2026-12-01T19:00"],
    ["endsAt", "2026-12-01T22:00"],
    ["sectionName", "VIP"],
    ["sectionCapacity", "2"],
    ["sectionPrice", "1000000"],
    ["sectionName", "STD"],
    ["sectionCapacity", "3"],
    ["sectionPrice", "300000"],
  ];
  for (const [k, v] of fields) data.append(k, v);
  return data;
}

beforeEach(() => {
  auth.mockReset();
  fetchMock.mockReset();
  vi.stubGlobal("fetch", fetchMock);
  const sections: { id: string; name: string; capacity: number }[] = [];
  fetchMock.mockImplementation(async (url: string, init: RequestInit) => {
    const path = new URL(url).pathname;
    if (path === "/catalog/events") return Response.json({ id: "ev-1" });
    if (path.endsWith("/seatmap/sections")) {
      const body = JSON.parse(String(init.body));
      sections.push({ id: `sec-${body.name}`, name: body.name, capacity: body.capacity });
      return Response.json({ id: "map-1", eventId: "ev-1", sections: [...sections] });
    }
    return Response.json({});
  });
});

describe("createEventAction", () => {
  it("refuses a signed-in user who is not an organizer", async () => {
    auth.mockResolvedValue({ accessToken: "t", roles: ["customer"] });

    await expect(createEventAction(form())).rejects.toThrow("Only organizers can create events.");
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("creates event, seat map, sections with their prices, inventory, then publishes", async () => {
    auth.mockResolvedValue({ accessToken: "t", roles: ["organizer"] });

    await expect(createEventAction(form())).rejects.toThrow("REDIRECT:/organizer");

    expect(calls().map((c) => c.path)).toEqual([
      "/catalog/events",
      "/catalog/events/ev-1/seatmap",
      "/catalog/events/ev-1/seatmap/sections",
      "/catalog/events/ev-1/price-tiers",
      "/catalog/events/ev-1/seatmap/sections",
      "/catalog/events/ev-1/price-tiers",
      "/reservation/events/ev-1/inventory",
      "/catalog/events/ev-1/publish",
    ]);
    const priceTiers = calls().filter((c) => c.path.endsWith("/price-tiers")).map((c) => c.body);
    expect(priceTiers).toEqual([
      { sectionId: "sec-VIP", amount: 1_000_000, currency: "VND" },
      { sectionId: "sec-STD", amount: 300_000, currency: "VND" },
    ]);
    expect(calls().find((c) => c.path.endsWith("/inventory"))?.body).toEqual({
      sections: [
        { sectionId: "sec-VIP", sectionName: "VIP", capacity: 2 },
        { sectionId: "sec-STD", sectionName: "STD", capacity: 3 },
      ],
    });
  });

  it("stops before publishing when a step fails", async () => {
    auth.mockResolvedValue({ accessToken: "t", roles: ["organizer"] });
    fetchMock.mockImplementation(async (url: string) =>
      new URL(url).pathname.endsWith("/seatmap") ? new Response(null, { status: 500 }) : Response.json({ id: "ev-1" }),
    );

    await expect(createEventAction(form())).rejects.toThrow("responded 500");
    expect(calls().some((c) => c.path.endsWith("/publish"))).toBe(false);
  });
});
