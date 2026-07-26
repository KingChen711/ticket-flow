import { expect, test } from "@playwright/test";

test("customer shell composes organizer and scanner zones without losing scanner BFF/PWA routes", async ({ page, request }) => {
  await page.goto("/organizer");
  await expect(page.getByText("TicketFlow — Organizer")).toBeVisible();
  await expect(page.getByText("Sign in with an organizer account to manage events.")).toBeVisible();

  await page.goto("/scanner");
  await expect(page.getByText("TicketFlow — Gate Scanner")).toBeVisible();
  await expect(page.getByText("Sign in with an organizer account to scan tickets.")).toBeVisible();

  const manifest = await request.get("/scanner/manifest.webmanifest");
  expect(manifest.status()).toBe(200);
  expect(await manifest.json()).toMatchObject({ start_url: "/scanner", scope: "/scanner/" });

  const icon = await request.get("/scanner-static/icon.svg");
  expect(icon.status()).toBe(200);
  expect(icon.headers()["content-type"]).toContain("image/svg+xml");

  const bff = await request.post("/scanner/api/validate", {
    data: {
      qrPayload: "not-a-ticket",
      eventId: "11111111-1111-4111-8111-111111111111",
      gateId: "north",
    },
  });
  expect(bff.status()).toBe(401);
  expect(await bff.json()).toMatchObject({ error: "Reauthentication required" });
});
