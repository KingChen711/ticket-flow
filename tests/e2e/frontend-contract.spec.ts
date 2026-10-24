import { expect, test } from "@playwright/test";

test("customer shell composes organizer and scanner zones without losing scanner BFF/PWA routes", async ({ page, request }) => {
  await page.goto("/organizer");
  await expect(page.getByText("TicketFlow — Organizer")).toBeVisible();
  await expect(page.getByText("Sign in with an organizer account to manage events.")).toBeVisible();

  await page.goto("/scanner/");
  await expect(page).toHaveURL(/\/scanner\/$/);
  await expect(page.getByText("TicketFlow — Gate Scanner")).toBeVisible();
  await expect(page.getByText("Sign in with a gate-staff or organizer account to scan tickets.")).toBeVisible();

  const manifest = await request.get("/scanner/manifest.webmanifest");
  expect(manifest.status()).toBe(200);
  expect(await manifest.json()).toMatchObject({ start_url: "/scanner/", scope: "/scanner/" });

  const icon = await request.get("/scanner-static/icon.svg");
  expect(icon.status()).toBe(200);
  expect(icon.headers()["content-type"]).toContain("image/svg+xml");

  const organizerSession = await request.get("/organizer/api/auth/session");
  expect(organizerSession.status()).toBe(200);
  expect(await organizerSession.json()).toBeNull();

  const scannerSession = await request.get("/scanner/api/auth/session");
  expect(scannerSession.status()).toBe(200);
  expect(await scannerSession.json()).toBeNull();

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

test("checkout reauthentication preserves the authoritative hold and idempotency key", async ({ request }) => {
  const holdId = "hold-from-reservation";
  const idempotencyKey = "stable-request-key";
  const response = await request.get(
    `/checkout?${new URLSearchParams({ holdId, idempotencyKey })}`,
    { maxRedirects: 0 },
  );

  expect([303, 307, 308]).toContain(response.status());
  const signIn = new URL(response.headers().location, "http://localhost:3000");
  expect(signIn.pathname).toBe("/api/auth/signin");
  expect(signIn.searchParams.get("callbackUrl")).toBe(
    `/checkout?${new URLSearchParams({ holdId, idempotencyKey })}`,
  );
});
