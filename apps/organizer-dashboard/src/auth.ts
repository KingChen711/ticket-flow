import NextAuth from "next-auth";
import Keycloak from "next-auth/providers/keycloak";

// Same host-only issuer situation as customer-web (docs/phase-8-plan.md mục 8/3 — no
// host-vs-Docker-network split since neither the browser nor this Next server run in Docker
// this phase) and the same real refresh-token rotation bug found while building customer-web's
// Commit 6 (access token TTL 1h, refresh TTL 30min — both expire during a long dev session if
// nothing refreshes them). Copied here rather than factored into a shared package: 2 call sites
// isn't yet worth a shared auth package (docs/phase-8-plan.md mục 6 — no premature abstraction).
async function refreshAccessToken(token: TokenSet) {
  const issuer = process.env.AUTH_KEYCLOAK_ISSUER;
  const res = await fetch(`${issuer}/protocol/openid-connect/token`, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({
      grant_type: "refresh_token",
      client_id: process.env.AUTH_KEYCLOAK_ID!,
      client_secret: process.env.AUTH_KEYCLOAK_SECRET!,
      refresh_token: token.refreshToken!,
    }),
  });

  if (!res.ok) return { ...token, error: "RefreshTokenError" as const };

  const refreshed = await res.json();
  return {
    ...token,
    accessToken: refreshed.access_token,
    refreshToken: refreshed.refresh_token ?? token.refreshToken,
    expiresAt: Math.floor(Date.now() / 1000) + refreshed.expires_in,
    roles: rolesFromAccessToken(refreshed.access_token),
    error: undefined,
  };
}

// This app needs to know the "organizer" realm role to gate the UI (a "customer" login must be
// blocked here, per docs/phase-8-plan.md Commit 7) — the "roles" client scope already puts
// realm_access.roles on the access token (infrastructure/keycloak/realm-export.json), so a plain
// JWT payload decode is enough; no need to also fetch userinfo or trust Auth.js's own `profile`
// shape (which only maps sub/name/email by default for the Keycloak provider).
function rolesFromAccessToken(accessToken: string | undefined): string[] {
  if (!accessToken) return [];
  const payload = accessToken.split(".")[1];
  if (!payload) return [];
  try {
    const decoded = JSON.parse(Buffer.from(payload, "base64url").toString("utf-8"));
    return decoded.realm_access?.roles ?? [];
  } catch {
    return [];
  }
}

interface TokenSet {
  accessToken?: string;
  refreshToken?: string;
  expiresAt?: number;
  roles?: string[];
  error?: "RefreshTokenError";
}

export const { handlers, signIn, signOut, auth } = NextAuth({
  providers: [Keycloak],
  callbacks: {
    async jwt({ token, account }) {
      if (account) {
        token.accessToken = account.access_token;
        token.refreshToken = account.refresh_token;
        token.expiresAt = account.expires_at;
        token.roles = rolesFromAccessToken(account.access_token as string);
        return token;
      }

      if (token.expiresAt && Date.now() / 1000 < (token.expiresAt as number) - 30) {
        return token;
      }

      return refreshAccessToken(token as TokenSet);
    },
    async session({ session, token }) {
      session.accessToken = token.accessToken as string;
      session.roles = (token.roles as string[] | undefined) ?? [];
      session.error = token.error as string | undefined;
      if (token.sub) session.userId = token.sub;
      return session;
    },
  },
});
