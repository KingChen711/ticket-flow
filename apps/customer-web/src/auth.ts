import NextAuth from "next-auth";
import Keycloak from "next-auth/providers/keycloak";

// Both the browser and this Next.js server run on the HOST (frontends aren't Dockerized this
// phase, see docs/phase-8-plan.md mục 8), so unlike Phase 7's backend services there is no
// host-vs-Docker-network issuer split here: every hop (browser to Keycloak, this server to
// Keycloak, this server to the gateway) uses the same host-facing localhost URLs.
// AUTH_KEYCLOAK_ISSUER is the plain host URL, not the keycloak:8180 one — confirmed via a real
// probe (Commit 3).
// Keycloak's access token TTL is 1 hour (Phase 7 realm-export.json) — found the hard way while
// probing Commit 6 (SignalR negotiate started 401'ing with NO code change on either side; turned
// out the session was just old enough for its embedded access_token to expire, and nothing was
// refreshing it). This is the real implementation of the refresh-token rotation Commit 3's plan
// section claimed Auth.js would provide but never actually wired up.
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
    // Keycloak doesn't always rotate the refresh token itself — keep the old one if absent.
    refreshToken: refreshed.refresh_token ?? token.refreshToken,
    expiresAt: Math.floor(Date.now() / 1000) + refreshed.expires_in,
    error: undefined,
  };
}

interface TokenSet {
  accessToken?: string;
  refreshToken?: string;
  expiresAt?: number;
  error?: "RefreshTokenError";
}

export const { handlers, signIn, signOut, auth } = NextAuth({
  providers: [Keycloak],
  // Real bug found wiring gate-scanner (Commit 8): browser cookies are scoped by hostname only,
  // NOT by port — so customer-web (:3000), organizer-dashboard (:3001), and gate-scanner (:3002)
  // all share ONE cookie jar on "localhost". Auth.js's default cookie name
  // (`authjs.session-token`) is identical across apps, so without a per-app name here, signing
  // into one app silently overwrites/reads the session cookie of whichever app last set it —
  // observed live as gate-scanner rendering "not an organizer" using customer-web's leftover
  // session on its very first load, before ever visiting it. Distinct cookie name per app fixes
  // the actual collision; distinct AUTH_SECRET per app (already the case) means a foreign cookie
  // also fails to decrypt rather than being silently accepted.
  cookies: {
    sessionToken: { name: "authjs.customer-web.session-token" },
  },
  callbacks: {
    // Access token and sub aren't on the session by default — pages need the token to call the
    // gateway as this user, and the sub (Keycloak user id) to ask for personalized recommendations.
    async jwt({ token, account }) {
      if (account) {
        token.accessToken = account.access_token;
        token.refreshToken = account.refresh_token;
        token.expiresAt = account.expires_at;
        return token;
      }

      if (token.expiresAt && Date.now() / 1000 < (token.expiresAt as number) - 30) {
        return token;
      }

      return refreshAccessToken(token as TokenSet);
    },
    async session({ session, token }) {
      session.accessToken = token.accessToken as string;
      session.error = token.error as string | undefined;
      if (token.sub) session.userId = token.sub;
      return session;
    },
  },
});
