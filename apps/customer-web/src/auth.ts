import NextAuth from "next-auth";
import Keycloak from "next-auth/providers/keycloak";

// Both the browser and this Next.js server run on the HOST (frontends aren't Dockerized this
// phase, see docs/phase-8-plan.md mục 8), so unlike Phase 7's backend services there is no
// host-vs-Docker-network issuer split here: every hop (browser to Keycloak, this server to
// Keycloak, this server to the gateway) uses the same host-facing localhost URLs.
// AUTH_KEYCLOAK_ISSUER is the plain host URL, not the keycloak:8180 one — confirmed via a real
// probe (Commit 3).
export const { handlers, signIn, signOut, auth } = NextAuth({
  providers: [Keycloak],
  callbacks: {
    // Access token and sub aren't on the session by default — pages need the token to call the
    // gateway as this user, and the sub (Keycloak user id) to ask for personalized recommendations.
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
      if (token.sub) session.userId = token.sub;
      return session;
    },
  },
});
