import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  // Workspace packages ship raw TS source, no build step (see docs/phase-8-plan.md mục 6) —
  // Next.js needs this to transpile them instead of treating them as pre-built node_modules.
  transpilePackages: ["@ticketflow/types", "@ticketflow/ui"],
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
      { source: "/organizer-static/:path*", destination: "http://localhost:3001/organizer-static/:path*" },
      { source: "/organizer/:path*", destination: "http://localhost:3001/organizer/:path*" },
      { source: "/organizer", destination: "http://localhost:3001/organizer" },
      { source: "/scanner-static/:path*", destination: "http://localhost:3002/scanner-static/:path*" },
      { source: "/scanner/:path*", destination: "http://localhost:3002/scanner/:path*" },
      { source: "/scanner", destination: "http://localhost:3002/scanner" },
    ];
  },
};

export default nextConfig;
