import type { NextConfig } from "next";

// Phase 11 Commit 2 — dockerize (Ch14). Zone destinations were hardcoded to `localhost:3001`/
// `3002` because every zone ran as a sibling `pnpm dev` process on the same host (Phase 8). In
// K8s each zone is its own pod — "localhost" from inside customer-web's container is itself, not
// the other zone — so the destination host must come from env (K8s Service DNS name), defaulting
// to the old localhost values so `pnpm dev` on the host keeps working unchanged.
const organizerZoneUrl = process.env.ORGANIZER_ZONE_URL ?? "http://localhost:3001";
const scannerZoneUrl = process.env.SCANNER_ZONE_URL ?? "http://localhost:3002";

const nextConfig: NextConfig = {
  // Docker multi-stage build (Dockerfile) copies only the standalone output — needs this to emit
  // a minimal server.js instead of assuming `next start` + full node_modules.
  output: "standalone",
  // Workspace packages ship raw TS source, no build step (see docs/phase-8-plan.md mục 6) —
  // Next.js needs this to transpile them instead of treating them as pre-built node_modules.
  transpilePackages: ["@ticketflow/types"],
  // Multi-Zones (docs/phase-8-plan.md Commit 9, mục 1) — customer-web is the shell, independent
  // deployability + edge composition instead of runtime JS federation (Module Federation probe,
  // Commit 3, confirmed dead for App Router). organizer-dashboard/gate-scanner each keep their
  // pages under a matching src/app/organizer|scanner/* folder and set assetPrefix
  // ("/organizer-static"/"/scanner-static") for their /_next/static output — 2 rewrite rules per
  // zone (pages + assets). Their /api/auth/* stays unprefixed and is never reached through this
  // shell (real bug found + fixed: routing basePath through both Next.js AND NextAuth doubled up
  // the strip and broke the OAuth callback — see the target apps' next.config.ts/.env.local).
  async rewrites() {
    return [
      { source: "/organizer-static/:path*", destination: `${organizerZoneUrl}/organizer-static/:path*` },
      { source: "/organizer/:path*", destination: `${organizerZoneUrl}/organizer/:path*` },
      { source: "/organizer", destination: `${organizerZoneUrl}/organizer` },
      { source: "/scanner-static/:path*", destination: `${scannerZoneUrl}/scanner-static/:path*` },
      { source: "/scanner/:path*", destination: `${scannerZoneUrl}/scanner/:path*` },
      { source: "/scanner", destination: `${scannerZoneUrl}/scanner` },
    ];
  },
};

export default nextConfig;
