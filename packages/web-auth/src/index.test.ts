import assert from "node:assert/strict";
import test from "node:test";
import {
  attachServerAccessToken,
  authCookieNames,
  refreshAccessTokenSingleFlight,
  sanitizeBrowserSessionResponse,
} from "./index.ts";

test("concurrent refreshes share one Keycloak request", async () => {
  let requests = 0;
  const fetcher: typeof fetch = async () => {
    requests++;
    await new Promise((resolve) => setTimeout(resolve, 20));
    return Response.json({ access_token: "new-access", refresh_token: "new-refresh", expires_in: 300 });
  };
  const token = { accessToken: "expired", refreshToken: "stable-refresh", expiresAt: 0 };

  const refreshed = await Promise.all(Array.from({ length: 12 }, () =>
    refreshAccessTokenSingleFlight(token, {
      issuer: "https://identity.example/realms/ticketflow",
      clientId: "customer-web",
      clientSecret: "secret",
      fetcher,
    })));

  assert.equal(requests, 1);
  assert.ok(refreshed.every((item) => item.accessToken === "new-access" && item.refreshToken === "new-refresh"));
});

test("refresh failure removes both bearer and refresh credentials", async () => {
  const result = await refreshAccessTokenSingleFlight(
    { accessToken: "expired", refreshToken: "invalid", expiresAt: 0 },
    {
      issuer: "https://identity.example/realms/ticketflow",
      clientId: "customer-web",
      clientSecret: "secret",
      fetcher: async () => new Response("invalid_grant", { status: 400 }),
    },
  );
  assert.equal(result.error, "RefreshTokenError");
  assert.equal(result.accessToken, undefined);
  assert.equal(result.refreshToken, undefined);
});

test("browser session endpoint strips server credentials and keeps cookie headers", async () => {
  const session = attachServerAccessToken({ user: { email: "buyer@example.com" } }, { accessToken: "secret-access" });
  const response = new Response(JSON.stringify({ ...session, refreshToken: "must-not-leak" }), {
    headers: { "Content-Type": "application/json", "Set-Cookie": "session=rotated; HttpOnly" },
  });
  const sanitized = await sanitizeBrowserSessionResponse(
    new Request("http://localhost:3000/api/auth/session"),
    response,
  );
  const body = await sanitized.json() as Record<string, unknown>;

  assert.equal(body.serverAccessToken, undefined);
  assert.equal(body.refreshToken, undefined);
  assert.deepEqual(body.user, { email: "buyer@example.com" });
  assert.match(sanitized.headers.get("set-cookie") ?? "", /HttpOnly/);
});

test("every OAuth cookie is namespaced per app", () => {
  const customer = Object.values(authCookieNames("customer-web")).map((cookie) => cookie.name);
  const organizer = new Set(Object.values(authCookieNames("organizer-dashboard")).map((cookie) => cookie.name));
  assert.equal(customer.length, 7);
  assert.ok(customer.every((name) => name.startsWith("authjs.customer-web.")));
  assert.ok(customer.every((name) => !organizer.has(name)));
});
