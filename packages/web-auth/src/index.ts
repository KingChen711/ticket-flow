export interface RefreshableToken extends Record<string, unknown> {
  accessToken?: string;
  refreshToken?: string;
  expiresAt?: number;
  roles?: string[];
  error?: "RefreshTokenError";
}

export interface RefreshTokenOptions {
  issuer: string;
  clientId: string;
  clientSecret: string;
  fetcher?: typeof fetch;
  timeoutMs?: number;
}

interface RefreshPayload {
  accessToken: string;
  refreshToken?: string;
  expiresIn: number;
}

const refreshFlights = new Map<string, Promise<RefreshPayload>>();

export async function refreshAccessTokenSingleFlight<T extends RefreshableToken>(
  token: T,
  options: RefreshTokenOptions,
): Promise<T> {
  if (!token.refreshToken) return failedToken(token);

  const flightKey = `${options.issuer}\u0000${options.clientId}\u0000${token.refreshToken}`;
  let flight = refreshFlights.get(flightKey);
  if (!flight) {
    flight = requestRefresh(token.refreshToken, options);
    refreshFlights.set(flightKey, flight);
    const clearFlight = () => {
      if (refreshFlights.get(flightKey) === flight) refreshFlights.delete(flightKey);
    };
    void flight.then(clearFlight, clearFlight);
  }

  try {
    const refreshed = await flight;
    return {
      ...token,
      accessToken: refreshed.accessToken,
      refreshToken: refreshed.refreshToken ?? token.refreshToken,
      expiresAt: Math.floor(Date.now() / 1000) + refreshed.expiresIn,
      roles: token.roles === undefined ? undefined : rolesFromAccessToken(refreshed.accessToken),
      error: undefined,
    };
  } catch {
    return failedToken(token);
  }
}

async function requestRefresh(
  refreshToken: string,
  { issuer, clientId, clientSecret, fetcher = fetch, timeoutMs = 8_000 }: RefreshTokenOptions,
): Promise<RefreshPayload> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(new DOMException("Refresh timed out", "TimeoutError")), timeoutMs);
  let response: Response;
  try {
    response = await fetcher(`${issuer}/protocol/openid-connect/token`, {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({
        grant_type: "refresh_token",
        client_id: clientId,
        client_secret: clientSecret,
        refresh_token: refreshToken,
      }),
      signal: controller.signal,
    });
  } finally {
    clearTimeout(timeout);
  }

  if (!response.ok) throw new Error(`Refresh endpoint responded ${response.status}.`);
  const payload = (await response.json()) as unknown;
  if (!isRecord(payload) || typeof payload.access_token !== "string" ||
      typeof payload.expires_in !== "number" || payload.expires_in <= 0) {
    throw new Error("Refresh endpoint returned an invalid payload.");
  }
  if (payload.refresh_token !== undefined && typeof payload.refresh_token !== "string") {
    throw new Error("Refresh endpoint returned an invalid refresh token.");
  }

  return {
    accessToken: payload.access_token,
    refreshToken: payload.refresh_token,
    expiresIn: payload.expires_in,
  };
}

function failedToken<T extends RefreshableToken>(token: T): T {
  return {
    ...token,
    accessToken: undefined,
    refreshToken: undefined,
    expiresAt: 0,
    error: "RefreshTokenError",
  };
}

export function attachServerAccessToken<T extends object>(
  session: T,
  token: RefreshableToken,
): T & { serverAccessToken?: string } {
  if (!token.error && token.accessToken) {
    Object.assign(session, { serverAccessToken: token.accessToken });
  }
  return session;
}

export async function sanitizeBrowserSessionResponse(request: Request, response: Response): Promise<Response> {
  if (!new URL(request.url).pathname.endsWith("/api/auth/session") || !response.ok) return response;

  const payload = (await response.clone().json()) as unknown;
  if (!isRecord(payload)) return response;
  delete payload.serverAccessToken;
  delete payload.accessToken;
  delete payload.refreshToken;

  const sanitized = new Response(JSON.stringify(payload), response);
  sanitized.headers.set("Content-Type", "application/json");
  sanitized.headers.delete("Content-Length");
  return sanitized;
}

export function authCookieNames(namespace: string) {
  const prefix = `authjs.${namespace}`;
  return {
    sessionToken: { name: `${prefix}.session-token` },
    callbackUrl: { name: `${prefix}.callback-url` },
    csrfToken: { name: `${prefix}.csrf-token` },
    pkceCodeVerifier: { name: `${prefix}.pkce.code_verifier` },
    state: { name: `${prefix}.state` },
    nonce: { name: `${prefix}.nonce` },
    webauthnChallenge: { name: `${prefix}.challenge` },
  };
}

export function rolesFromAccessToken(accessToken: string | undefined): string[] {
  if (!accessToken) return [];
  const payload = accessToken.split(".")[1];
  if (!payload) return [];

  try {
    const decoded = JSON.parse(Buffer.from(payload, "base64url").toString("utf-8")) as unknown;
    if (!isRecord(decoded) || !isRecord(decoded.realm_access) || !Array.isArray(decoded.realm_access.roles)) {
      return [];
    }
    return decoded.realm_access.roles.filter((role): role is string => typeof role === "string");
  } catch {
    return [];
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
