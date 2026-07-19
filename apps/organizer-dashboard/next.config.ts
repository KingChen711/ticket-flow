import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  // Phase 11 Commit 2 — Dockerfile copies only the standalone output.
  output: "standalone",
  // Workspace packages ship raw TS source, no build step (see docs/phase-8-plan.md mục 6) —
  // Next.js needs this to transpile them instead of treating them as pre-built node_modules.
  transpilePackages: ["@ticketflow/types", "@ticketflow/ui"],
  // Multi-Zones (docs/phase-8-plan.md Commit 9, mục 1): customer-web is the shell and rewrites
  // /organizer/* here. Pages already live under src/app/organizer/* (manual prefix, not Next's
  // `basePath`) — `basePath` was tried first and reverted: it also shifts API routes, and
  // NextAuth's own request parser trips over the double-stripped path once Next's router already
  // strips basePath before the route handler ever sees the request (real bug hit live —
  // `UnknownAction: Cannot parse action at /api/auth/callback/keycloak`). `/api/auth/*` stays
  // unprefixed on purpose so Keycloak's registered redirect URI never has to change.
  // assetPrefix only moves /_next/static/* under /organizer-static so it can't collide with the
  // shell's or gate-scanner's own /_next/* once proxied under the same origin.
  assetPrefix: "/organizer-static",
};

export default nextConfig;
