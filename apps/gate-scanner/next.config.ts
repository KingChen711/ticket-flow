import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  // Phase 11 Commit 2 — Dockerfile copies only the standalone output.
  output: "standalone",
  // Workspace packages ship raw TS source, no build step (see docs/phase-8-plan.md mục 6) —
  // Next.js needs this to transpile them instead of treating them as pre-built node_modules.
  transpilePackages: ["@ticketflow/http-client", "@ticketflow/types", "@ticketflow/ui", "@ticketflow/web-auth"],
  // Multi-Zones (docs/phase-8-plan.md Commit 9, mục 1) — same shape as organizer-dashboard's
  // next.config.ts (see its comment for why `basePath` was tried and reverted). Pages live under
  // src/app/scanner/*; /api/auth/* stays unprefixed at the app root. The customer shell maps its
  // public /scanner/api/* BFF path back to this app's root /api/* route.
  assetPrefix: "/scanner-static",
  async rewrites() {
    return [
      { source: "/scanner/api/:path*", destination: "/api/:path*" },
      // Public files are rooted at / in a standalone Next deployment. Keep the scanner's PWA
      // icon inside its mount when it is reached through the customer shell.
      { source: "/scanner-static/icon.svg", destination: "/icon.svg" },
    ];
  },
};

export default nextConfig;
