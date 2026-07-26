import assert from "node:assert/strict";
import { createServer } from "node:http";
import test from "node:test";
import { fetchJson, HttpError } from "./index.ts";

test("non-success status maps conflict details", async () => {
  await withServer((request, response) => {
    response.writeHead(409, { "Content-Type": "application/json" });
    response.end(JSON.stringify({ error: "idempotency key mismatch" }));
  }, async (url) => {
    await assert.rejects(
      fetchJson(url, { validate: (value) => value }),
      (error: unknown) => error instanceof HttpError && error.kind === "conflict" && error.status === 409,
    );
  });
});

test("runtime validator failure is an invalid upstream response", async () => {
  await assert.rejects(
    fetchJson("data:application/json,%7B%22wrong%22%3Atrue%7D", {
      validate: () => { throw new TypeError("drift"); },
    }),
    (error: unknown) => error instanceof HttpError && error.kind === "invalid-response" && error.status === 502,
  );
});

test("timeout aborts a hanging request", async () => {
  await withServer(() => undefined, async (url) => {
    await assert.rejects(
      fetchJson(url, { timeoutMs: 25, validate: (value) => value }),
      (error: unknown) => error instanceof HttpError && error.kind === "timeout",
    );
  });
});

async function withServer(
  handler: Parameters<typeof createServer>[0],
  action: (url: string) => Promise<void>,
) {
  const server = createServer(handler);
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const address = server.address();
  if (!address || typeof address === "string") throw new Error("Test server did not bind a TCP port.");
  try {
    await action(`http://127.0.0.1:${address.port}`);
  } finally {
    server.closeAllConnections();
    await new Promise<void>((resolve, reject) => server.close((error) => error ? reject(error) : resolve()));
  }
}
