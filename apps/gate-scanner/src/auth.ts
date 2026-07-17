import NextAuth from "next-auth";
import Keycloak from "next-auth/providers/keycloak";

// Same shape as organizer-dashboard's auth.ts (docs/phase-8-plan.md Commit 7) — host-only
// issuer, real refresh-token rotation, roles decoded straight from the access token JWT
// payload (realm_access.roles, "roles" client scope already puts it on the token for all 3
// apps). POST /tickets/validate is RequireOrganizer (no dedicated gate-staff role yet, backlog
// docs/phase-8-plan.md mục 4), so this app gates on "organizer" same as organizer-dashboard.
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
  // Cookies are scoped by hostname only, not port — customer-web/organizer-dashboard/gate-scanner
  // all share one cookie jar on "localhost". Without a per-app name, Auth.js's default session
  // cookie name collides across all 3 apps — this is in fact how the bug was FOUND: gate-scanner
  // rendered "not an organizer" on its very first-ever page load, before this app was ever visited,
  // because it decrypted customer-web's leftover session cookie (same name, same AUTH_SECRET at
  // the time). Fixed here and backported to the other 2 apps (docs/phase-8-plan.md Commit 8).
  cookies: {
    sessionToken: { name: "authjs.gate-scanner.session-token" },
  },
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
