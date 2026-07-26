import { fetchJson, type RuntimeValidator } from "@ticketflow/http-client";

const GATEWAY_URL = process.env.GATEWAY_URL ?? "http://localhost:8080";

export function fetchGateway<T>(path: string, validate: RuntimeValidator<T>, signal?: AbortSignal): Promise<T> {
  return fetchJson(`${GATEWAY_URL}${path}`, {
    cache: "no-store",
    signal,
    validate,
  });
}

export function fetchGatewayAuthed<T>(
  path: string,
  accessToken: string,
  validate: RuntimeValidator<T>,
  init: Omit<RequestInit, "signal"> & { signal?: AbortSignal; timeoutMs?: number } = {},
): Promise<T> {
  const { timeoutMs, ...requestInit } = init;
  return fetchJson(`${GATEWAY_URL}${path}`, {
    ...requestInit,
    cache: "no-store",
    headers: {
      ...init.headers,
      Authorization: `Bearer ${accessToken}`,
    },
    timeoutMs,
    validate,
  });
}
