import { execFile } from "node:child_process";
import { mkdir, writeFile } from "node:fs/promises";
import path from "node:path";
import { promisify } from "node:util";
import { expect, request as playwrightRequest, test, type APIRequestContext, type Page } from "@playwright/test";

const execFileAsync = promisify(execFile);
const gatewayUrl = process.env.E2E_GATEWAY_URL ?? "http://127.0.0.1:8080";
const keycloakUrl = process.env.E2E_KEYCLOAK_URL ?? "http://localhost:8180";
const customerUsername = process.env.E2E_CUSTOMER_USERNAME ?? "customer1";
const customerPassword = process.env.E2E_CUSTOMER_PASSWORD ?? "Customer1!Dev";
const organizerUsername = process.env.E2E_ORGANIZER_USERNAME ?? "organizer1";
const organizerPassword = process.env.E2E_ORGANIZER_PASSWORD ?? "Organizer1!Dev";
const internalClientId = process.env.E2E_INTERNAL_CLIENT_ID ?? "ticketflow-internal";
const internalClientSecret = process.env.E2E_INTERNAL_CLIENT_SECRET ?? "internal-services-dev-secret";
const outputDirectory = path.resolve("output/e2e");

test.describe.configure({ mode: "serial" });

test("login, publish, hold, idempotent checkout, payment, PDF and one-time scanner check-in", async ({ browser, page }) => {
  const api = await playwrightRequest.newContext({ baseURL: gatewayUrl });
  const authApi = await playwrightRequest.newContext({ baseURL: keycloakUrl });
  try {
    const organizerToken = await accessToken(authApi, organizerUsername, organizerPassword);
    const customerToken = await accessToken(authApi, customerUsername, customerPassword);
    const serviceToken = await serviceAccessToken(authApi);
    const fixture = await createSaleableEvent(api, organizerToken, serviceToken, customerToken, 2);

    await page.goto(`/events/${fixture.eventId}`);
    await login(page, customerUsername, customerPassword);
    await expect(page.getByRole("heading", { name: fixture.title })).toBeVisible();

    await page.locator('input[name="seatIds"]').first().check({ force: true });
    await page.getByRole("button", { name: "Hold seats & continue" }).click();
    await expect(page).toHaveURL(/\/checkout\?/);
    const checkoutUrl = new URL(page.url());
    const holdId = requiredQuery(checkoutUrl, "holdId");
    const idempotencyKey = requiredQuery(checkoutUrl, "idempotencyKey");

    const checkoutBody = { holdId, paymentMethod: "credit-card", idempotencyKey };
    const duplicateResponses = await Promise.all([
      postJson(api, "/ordering/orders/", checkoutBody, customerToken),
      postJson(api, "/ordering/orders/", checkoutBody, customerToken),
    ]);
    const duplicateOrderIds = duplicateResponses.map((response) => String(response.orderId));
    expect(new Set(duplicateOrderIds).size).toBe(1);
    const orderId = duplicateOrderIds[0];

    // The browser submits the same stable key a third time. It must land on the original order,
    // never mint another stream because the first response happened outside this page.
    await page.getByRole("button", { name: "Place order" }).click();
    await expect(page).toHaveURL(new RegExp(`/orders/${orderId}$`));
    await expect(page.locator("p", { hasText: "Status:" }).locator("span")).toHaveText("Confirmed", { timeout: 180_000 });
    await expect(page.getByRole("link", { name: "View PDF" })).toBeVisible();

    const tickets = await getJson<Array<{ id: string; orderId: string; seatId: string }>>(
      api,
      `/ticketing/orders/${orderId}/tickets`,
      customerToken,
    );
    expect(tickets).toHaveLength(1);
    expect(tickets[0].orderId).toBe(orderId);

    const pdfResponse = await api.get(`/ticketing/tickets/${tickets[0].id}/pdf`, {
      headers: bearer(customerToken),
    });
    expect(pdfResponse.status()).toBe(200);
    expect(pdfResponse.headers()["content-type"]).toContain("application/pdf");
    const pdfBytes = await pdfResponse.body();
    expect(pdfBytes.subarray(0, 5).toString("ascii")).toBe("%PDF-");

    await mkdir(outputDirectory, { recursive: true });
    const pdfPath = path.join(outputDirectory, `ticket-${tickets[0].id}.pdf`);
    const qrImagePath = path.join(outputDirectory, `ticket-${tickets[0].id}.png`);
    await writeFile(pdfPath, pdfBytes);
    await renderFirstPdfPage(pdfPath, qrImagePath);

    // Scanner has its own NextAuth cookie namespace and callback endpoint, so authenticate on its
    // independently deployable origin. The shell route itself is covered by frontend-contract.
    const scannerContext = await browser.newContext();
    const scanner = await scannerContext.newPage();
    await scanner.goto("http://localhost:3002/scanner");
    await login(scanner, organizerUsername, organizerPassword);
    await scanner.goto(`http://localhost:3002/scanner?eventId=${fixture.eventId}&gate=North%20Gate`);
    await scanner.locator('input[type="file"]').setInputFiles(qrImagePath);
    await expect(scanner.getByText("VALID — checked in")).toBeVisible({ timeout: 30_000 });
    await scanner.getByRole("button", { name: "Scan next" }).click();
    await scanner.locator('input[type="file"]').setInputFiles(qrImagePath);
    await expect(scanner.getByText("ALREADY CHECKED IN")).toBeVisible({ timeout: 30_000 });
    await scannerContext.close();

    await writeSeed({
      scenario: "happy-path-one-time-check-in",
      eventId: fixture.eventId,
      seatIds: [tickets[0].seatId],
      holdId,
      idempotencyKey,
      orderIds: [orderId],
      ticketIds: [tickets[0].id],
      fixture: fixture.seed,
    });
  } finally {
    await api.dispose();
    await authApi.dispose();
  }
});

test("event cancellation refunds an authorized purchase and revokes its ticket", async ({ browser }) => {
  const api = await playwrightRequest.newContext({ baseURL: gatewayUrl });
  const authApi = await playwrightRequest.newContext({ baseURL: keycloakUrl });
  try {
    const organizerToken = await accessToken(authApi, organizerUsername, organizerPassword);
    const customerToken = await accessToken(authApi, customerUsername, customerPassword);
    const serviceToken = await serviceAccessToken(authApi);
    const fixture = await createSaleableEvent(api, organizerToken, serviceToken, customerToken, 1);
    const purchase = await purchaseFirstSeat(api, fixture, customerToken);
    const confirmed = await waitForOrder(api, purchase.orderId, customerToken, "Confirmed");
    expect(confirmed.status).toBe("Confirmed");

    const tickets = await getJson<Array<{ id: string; seatId: string }>>(
      api,
      `/ticketing/orders/${purchase.orderId}/tickets`,
      customerToken,
    );
    const pdf = await api.get(`/ticketing/tickets/${tickets[0].id}/pdf`, { headers: bearer(customerToken) });
    expect(pdf.status()).toBe(200);
    await mkdir(outputDirectory, { recursive: true });
    const pdfPath = path.join(outputDirectory, `cancelled-ticket-${tickets[0].id}.pdf`);
    const qrImagePath = path.join(outputDirectory, `cancelled-ticket-${tickets[0].id}.png`);
    await writeFile(pdfPath, await pdf.body());
    await renderFirstPdfPage(pdfPath, qrImagePath);

    const started = await postJson<{ workflowId: string }>(
      api,
      `/catalog/events/${fixture.eventId}/cancel?reason=phase-12-e2e`,
      {},
      organizerToken,
      [202],
    );
    const workflow = await waitForWorkflow(api, started.workflowId, organizerToken);
    expect(workflow).toMatchObject({ status: "Completed", orderCount: 1 });

    const scannerContext = await browser.newContext();
    const scanner = await scannerContext.newPage();
    await scanner.goto("http://localhost:3002/scanner");
    await login(scanner, organizerUsername, organizerPassword);
    await scanner.goto(`http://localhost:3002/scanner?eventId=${fixture.eventId}&gate=Refund%20Gate`);
    await scanner.locator('input[type="file"]').setInputFiles(qrImagePath);
    await expect(scanner.getByText("REVOKED")).toBeVisible({ timeout: 30_000 });
    await scannerContext.close();

    await writeSeed({
      scenario: "event-cancellation-refund",
      eventId: fixture.eventId,
      seatIds: [tickets[0].seatId],
      holdId: purchase.holdId,
      idempotencyKey: purchase.idempotencyKey,
      orderIds: [purchase.orderId],
      ticketIds: [tickets[0].id],
      cancellationWorkflowId: started.workflowId,
      fixture: fixture.seed,
    });
  } finally {
    await api.dispose();
    await authApi.dispose();
  }
});

test("reservation outage is rendered as unavailable rather than sold out and recovers", async ({ page }) => {
  const api = await playwrightRequest.newContext({ baseURL: gatewayUrl });
  const authApi = await playwrightRequest.newContext({ baseURL: keycloakUrl });
  let stopped = false;
  let recoveryProbe: { eventId: string; token: string } | undefined;
  try {
    const organizerToken = await accessToken(authApi, organizerUsername, organizerPassword);
    const customerToken = await accessToken(authApi, customerUsername, customerPassword);
    const serviceToken = await serviceAccessToken(authApi);
    const fixture = await createSaleableEvent(api, organizerToken, serviceToken, customerToken, 1);
    recoveryProbe = { eventId: fixture.eventId, token: organizerToken };
    await docker("stop", "ticketflow-reservation");
    stopped = true;

    await page.goto(`/events/${fixture.eventId}`);
    await expect(page.getByText("Seat availability is temporarily unavailable.")).toBeVisible({ timeout: 30_000 });
    await expect(page.getByText(/0 of 0 seats available/)).toHaveCount(0);
  } finally {
    if (stopped) {
      await docker("start", "ticketflow-reservation");
      await waitForAvailability(api, recoveryProbe!);
    }
    await api.dispose();
    await authApi.dispose();
  }
});

async function login(page: Page, username: string, password: string) {
  if (await page.getByRole("button", { name: "Sign in" }).isVisible()) {
    await page.getByRole("button", { name: "Sign in" }).click();
    await page.locator("#username").fill(username);
    await page.locator("#password").fill(password);
    await page.locator("#kc-login").click();
  }
  await expect(page.getByText(new RegExp(`${username.replace(/\d+$/, "")}.*@ticketflow\\.dev`, "i"))).toBeVisible();
}

async function accessToken(api: APIRequestContext, username: string, password: string) {
  const response = await api.post("/realms/ticketflow/protocol/openid-connect/token", {
    form: { grant_type: "password", client_id: "ticketflow-dev", username, password },
  });
  expect(response.status(), await response.text()).toBe(200);
  const payload = await response.json() as { access_token?: string };
  if (!payload.access_token) throw new Error(`Keycloak returned no access token for ${username}.`);
  return payload.access_token;
}

async function serviceAccessToken(api: APIRequestContext) {
  const response = await api.post("/realms/ticketflow/protocol/openid-connect/token", {
    form: {
      grant_type: "client_credentials",
      client_id: internalClientId,
      client_secret: internalClientSecret,
    },
  });
  expect(response.status(), await response.text()).toBe(200);
  const payload = await response.json() as { access_token?: string };
  if (!payload.access_token) throw new Error("Keycloak returned no internal service access token.");
  return payload.access_token;
}

async function createSaleableEvent(
  api: APIRequestContext,
  organizerToken: string,
  serviceToken: string,
  buyerProbeToken: string,
  capacity: number,
) {
  const suffix = `${Date.now()}-${Math.random().toString(16).slice(2, 8)}`;
  const title = `Phase 12 E2E ${suffix}`;
  const startsAt = new Date(Date.now() + 30 * 24 * 60 * 60_000);
  const venue = await postJson<{ id: string }>(api, "/catalog/venues/", {
    name: `E2E Venue ${suffix}`,
    street: "18 Reliability Road",
    city: "Ho Chi Minh City",
    country: "VN",
    postalCode: "700000",
  }, organizerToken, [201]);
  const event = await postJson<{ id: string }>(api, "/catalog/events/", {
    title,
    description: "Reproducible Phase 12 full-stack fixture.",
    artist: "TicketFlow E2E",
    venueId: venue.id,
    startsAt: startsAt.toISOString(),
    endsAt: new Date(startsAt.getTime() + 2 * 60 * 60_000).toISOString(),
  }, organizerToken, [201]);
  await postJson(api, `/catalog/events/${event.id}/seatmap/`, {}, organizerToken, [200, 201]);
  const seatMap = await postJson<{ sections: Array<{ id: string; name: string; capacity: number }> }>(
    api,
    `/catalog/events/${event.id}/seatmap/sections`,
    { name: "E2E General Admission", capacity },
    organizerToken,
    [200, 201],
  );
  const section = seatMap.sections.find((candidate) => candidate.name === "E2E General Admission")
    ?? seatMap.sections.at(-1);
  if (!section) throw new Error("Catalog returned no E2E section.");
  await postJson(api, `/catalog/events/${event.id}/price-tiers`, {
    sectionId: section.id,
    amount: 100_000,
    currency: "VND",
  }, organizerToken);
  await postJson(api, `/catalog/events/${event.id}/publish`, {}, organizerToken);
  await postJson(api, `/reservation/events/${event.id}/inventory`, {
    sections: [{ sectionId: section.id, sectionName: section.name, capacity: section.capacity }],
  }, serviceToken);
  const availability = await getJson<{ seats: Array<{ id: string }> }>(
    api,
    `/reservation/events/${event.id}/availability`,
    organizerToken,
  );
  expect(availability.seats).toHaveLength(capacity);
  await waitForSalePolicy(api, event.id, availability.seats[0].id, buyerProbeToken);
  return {
    eventId: event.id,
    title,
    seatIds: availability.seats.map((seat) => seat.id),
    seed: { venueId: venue.id, sectionId: section.id, capacity, amount: 100_000, currency: "VND" },
  };
}

async function waitForSalePolicy(
  api: APIRequestContext,
  eventId: string,
  seatId: string,
  token: string,
) {
  const hold = await poll(async () => {
    const response = await api.post("/reservation/holds", {
      data: { eventId, seatIds: [seatId] },
      headers: bearer(token),
    });
    const body = await response.text();
    if (response.status() === 400 && body.includes("no sale policy snapshot")) return null;
    expect(response.status(), `/reservation/holds readiness probe: ${body}`).toBe(200);
    const result = JSON.parse(body) as { holdId: string; success: boolean };
    expect(result.success).toBe(true);
    return result;
  }, 30_000, `sale policy ${eventId} to reach Reservation`);

  const released = await api.post(`/reservation/holds/${hold.holdId}/release`, {
    headers: bearer(token),
  });
  expect(released.status(), await released.text()).toBe(204);
}

async function purchaseFirstSeat(
  api: APIRequestContext,
  fixture: Awaited<ReturnType<typeof createSaleableEvent>>,
  customerToken: string,
) {
  const hold = await postJson<{ holdId: string; success: boolean }>(api, "/reservation/holds", {
    eventId: fixture.eventId,
    seatIds: [fixture.seatIds[0]],
  }, customerToken);
  expect(hold.success).toBe(true);
  const idempotencyKey = crypto.randomUUID();
  const order = await postJson<{ orderId: string }>(api, "/ordering/orders/", {
    holdId: hold.holdId,
    paymentMethod: "credit-card",
    idempotencyKey,
  }, customerToken, [201]);
  return { orderId: order.orderId, holdId: hold.holdId, idempotencyKey };
}

async function waitForOrder(api: APIRequestContext, orderId: string, token: string, expected: string) {
  return poll(async () => {
    const response = await api.get(`/ordering/orders/${orderId}`, { headers: bearer(token) });
    if (response.status() !== 200) return null;
    const order = await response.json() as { status: string };
    return order.status === expected ? order : null;
  }, 180_000, `order ${orderId} to become ${expected}`);
}

async function waitForWorkflow(api: APIRequestContext, workflowId: string, token: string) {
  return poll(async () => {
    const response = await api.get(`/catalog/workflows/event-cancellations/${workflowId}`, { headers: bearer(token) });
    if (response.status() !== 200) return null;
    const workflow = await response.json() as { status: string; orderCount: number };
    return workflow.status === "Completed" ? workflow : null;
  }, 180_000, `cancellation workflow ${workflowId} to complete`);
}

async function postJson<T = Record<string, unknown>>(
  api: APIRequestContext,
  url: string,
  data: unknown,
  token: string,
  expectedStatuses = [200, 201],
): Promise<T> {
  const response = await api.post(url, { data, headers: bearer(token) });
  const body = await response.text();
  expect(expectedStatuses, `${url}: HTTP ${response.status()} ${body}`).toContain(response.status());
  return body ? JSON.parse(body) as T : {} as T;
}

async function getJson<T>(api: APIRequestContext, url: string, token: string): Promise<T> {
  const response = await api.get(url, { headers: bearer(token) });
  const body = await response.text();
  expect(response.status(), `${url}: ${body}`).toBe(200);
  return JSON.parse(body) as T;
}

function bearer(token: string) {
  return { Authorization: `Bearer ${token}` };
}

function requiredQuery(url: URL, key: string) {
  const value = url.searchParams.get(key);
  if (!value) throw new Error(`Checkout URL did not contain ${key}.`);
  return value;
}

async function poll<T>(probe: () => Promise<T | null>, timeoutMs: number, description: string): Promise<T> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const result = await probe();
    if (result !== null) return result;
    await new Promise((resolve) => setTimeout(resolve, 1_000));
  }
  throw new Error(`Timed out waiting for ${description}.`);
}

async function renderFirstPdfPage(pdfPath: string, pngPath: string) {
  const outputPrefix = pngPath.replace(/\.png$/i, "");
  const renderer = process.env.E2E_PDFTOPPM ?? "pdftoppm";
  await execFileAsync(renderer, ["-f", "1", "-singlefile", "-png", "-r", "150", pdfPath, outputPrefix]);
}

async function docker(action: "start" | "stop", container: string) {
  await execFileAsync("docker", [action, container]);
}

async function waitForAvailability(
  api: APIRequestContext,
  probe: { eventId: string; token: string },
) {
  await poll(async () => {
    try {
      const response = await api.get(`/reservation/events/${probe.eventId}/availability`, {
        headers: bearer(probe.token),
      });
      return response.status() === 200 ? true : null;
    } catch {
      return null;
    }
  }, 120_000, `reservation availability for ${probe.eventId} to recover`);
}

async function writeSeed(seed: Record<string, unknown>) {
  await mkdir(outputDirectory, { recursive: true });
  const pathName = path.join(outputDirectory, `${String(seed.scenario)}-seed.json`);
  await writeFile(pathName, JSON.stringify({ generatedAt: new Date().toISOString(), ...seed }, null, 2));
}
