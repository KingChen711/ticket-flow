import { fetchJson, type RuntimeValidator } from "@ticketflow/http-client";

const GATEWAY_URL = process.env.GATEWAY_URL ?? "http://localhost:8080";

export function fetchGateway<T>(
  path: string,
  validate: RuntimeValidator<T>,
  options: { accessToken?: string; signal?: AbortSignal; timeoutMs?: number } = {},
): Promise<T> {
  return fetchJson(`${GATEWAY_URL}${path}`, {
    cache: "no-store",
    headers: options.accessToken ? { Authorization: `Bearer ${options.accessToken}` } : undefined,
    signal: options.signal,
    timeoutMs: options.timeoutMs,
    validate,
  });
}

export function mutateGateway<T>(
  path: string,
  accessToken: string,
  body: unknown,
  validate: RuntimeValidator<T>,
  options: { signal?: AbortSignal; timeoutMs?: number } = {},
): Promise<T> {
  return fetchJson(`${GATEWAY_URL}${path}`, {
    method: "POST",
    cache: "no-store",
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${accessToken}`,
    },
    body: JSON.stringify(body),
    signal: options.signal,
    timeoutMs: options.timeoutMs,
    validate,
  });
}
