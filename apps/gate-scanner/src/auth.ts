import NextAuth from "next-auth";
import Keycloak from "next-auth/providers/keycloak";
import {
  attachServerAccessToken,
  authCookieNames,
  refreshAccessTokenSingleFlight,
  rolesFromAccessToken,
  sanitizeBrowserSessionResponse,
  type RefreshableToken,
} from "@ticketflow/web-auth";

const nextAuth = NextAuth({
  basePath: "/scanner/api/auth",
  providers: [Keycloak],
  cookies: authCookieNames("gate-scanner"),
  callbacks: {
    async jwt({ token, account }) {
      if (account) {
        token.accessToken = account.access_token;
        token.refreshToken = account.refresh_token;
        token.expiresAt = account.expires_at;
        token.roles = rolesFromAccessToken(account.access_token);
        token.error = undefined;
        return token;
      }

      if (token.expiresAt && Date.now() / 1000 < (token.expiresAt as number) - 30) return token;

      return refreshAccessTokenSingleFlight(token as RefreshableToken, {
        issuer: process.env.AUTH_KEYCLOAK_ISSUER!,
        clientId: process.env.AUTH_KEYCLOAK_ID!,
        clientSecret: process.env.AUTH_KEYCLOAK_SECRET!,
      });
    },
    async session({ session, token }) {
      session.roles = (token.roles as string[] | undefined) ?? [];
      session.error = token.error as "RefreshTokenError" | undefined;
      if (token.sub) session.userId = token.sub;
      return attachServerAccessToken(session, token as RefreshableToken);
    },
  },
});

export const { signIn, signOut, auth } = nextAuth;
export const handlers = {
  GET: async (...args: Parameters<typeof nextAuth.handlers.GET>) =>
    sanitizeBrowserSessionResponse(args[0], await nextAuth.handlers.GET(...args)),
  POST: nextAuth.handlers.POST,
};
