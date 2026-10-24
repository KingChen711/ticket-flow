export type RuntimeValidator<T> = (value: unknown) => T;

export type HttpErrorKind =
  | "aborted"
  | "conflict"
  | "invalid-response"
  | "network"
  | "rate-limited"
  | "timeout"
  | "unauthorized"
  | "unavailable"
  | "unexpected";

export class HttpError extends Error {
  constructor(
    message: string,
    public readonly kind: HttpErrorKind,
    public readonly status: number | null = null,
    public readonly details: unknown = null,
    options?: ErrorOptions,
  ) {
    super(message, options);
    this.name = "HttpError";
  }
}

export interface FetchJsonOptions<T> extends Omit<RequestInit, "signal"> {
  signal?: AbortSignal;
  timeoutMs?: number;
  validate: RuntimeValidator<T>;
}

const DEFAULT_TIMEOUT_MS = 8_000;

export async function fetchJson<T>(
  input: string | URL | Request,
  { validate, timeoutMs = DEFAULT_TIMEOUT_MS, signal, ...init }: FetchJsonOptions<T>,
): Promise<T> {
  const controller = new AbortController();
  let timedOut = false;
  const timeout = setTimeout(() => {
    timedOut = true;
    controller.abort(new DOMException("Request timed out", "TimeoutError"));
  }, timeoutMs);

  const abort = () => controller.abort(signal?.reason);
  if (signal?.aborted) abort();
  else signal?.addEventListener("abort", abort, { once: true });

  try {
    let response: Response;
    try {
      response = await fetch(input, { ...init, signal: controller.signal });
    } catch (error) {
      if (timedOut) {
        throw new HttpError(`Request timed out after ${timeoutMs} ms.`, "timeout", 504, null, {
          cause: error,
        });
      }
      if (signal?.aborted || controller.signal.aborted) {
        throw new HttpError("Request was cancelled.", "aborted", null, null, { cause: error });
      }
      throw new HttpError("The service could not be reached.", "network", null, null, {
        cause: error,
      });
    }

    const payload = await readPayload(response);
    if (!response.ok) {
      const message = errorMessage(payload) ?? `Service responded ${response.status}.`;
      throw new HttpError(message, kindForStatus(response.status), response.status, payload);
    }

    try {
      return validate(payload);
    } catch (error) {
      throw new HttpError(
        "The service returned a response that does not match the expected contract.",
        "invalid-response",
        502,
        payload,
        { cause: error },
      );
    }
  } finally {
    clearTimeout(timeout);
    signal?.removeEventListener("abort", abort);
  }
}

async function readPayload(response: Response): Promise<unknown> {
  if (response.status === 204) return undefined;

  const text = await response.text();
  if (text.length === 0) return undefined;

  try {
    return JSON.parse(text) as unknown;
  } catch {
    return text;
  }
}

function errorMessage(payload: unknown): string | null {
  if (typeof payload === "string" && payload.trim()) return payload.trim();
  if (!isRecord(payload)) return null;

  for (const field of ["error", "detail", "title", "message"] as const) {
    if (typeof payload[field] === "string" && payload[field].trim()) return payload[field].trim();
  }
  return null;
}

function kindForStatus(status: number): HttpErrorKind {
  if (status === 401) return "unauthorized";
  if (status === 409) return "conflict";
  if (status === 429) return "rate-limited";
  if (status === 502 || status === 503 || status === 504) return "unavailable";
  return "unexpected";
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
