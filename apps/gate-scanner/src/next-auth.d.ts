export {};

declare module "next-auth" {
  interface Session {
    serverAccessToken?: string;
    userId?: string;
    roles?: string[];
    error?: "RefreshTokenError";
  }
}

declare module "next-auth/jwt" {
  interface JWT {
    accessToken?: string;
    refreshToken?: string;
    expiresAt?: number;
    roles?: string[];
    error?: "RefreshTokenError";
  }
}
