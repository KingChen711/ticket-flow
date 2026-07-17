import NextAuth from "next-auth";
import Keycloak from "next-auth/providers/keycloak";

// Probe app (Phase 8 Commit 3) — both the browser and this Next.js server run on the HOST
// (frontends aren't Dockerized this phase, see docs/phase-8-plan.md mục 8), so unlike Phase 7's
// backend services there is no host-vs-Docker-network issuer split here: every hop (browser to
// Keycloak, this server to Keycloak, this server to the gateway) uses the same host-facing
// localhost URLs. AUTH_KEYCLOAK_ISSUER is the plain host URL, not the keycloak:8180 one.
export const { handlers, signIn, signOut, auth } = NextAuth({
  providers: [Keycloak],
  callbacks: {
    // Access token isn't on the session by default — the probe needs it to call the gateway.
    async jwt({ token, account }) {
      if (account) {
        token.accessToken = account.access_token;
        token.refreshToken = account.refresh_token;
        token.expiresAt = account.expires_at;
      }
      return token;
    },
    async session({ session, token }) {
      session.accessToken = token.accessToken as string;
      return session;
    },
  },
});
