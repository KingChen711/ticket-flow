import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  // Phase 11 Commit 2 — Dockerfile copies only the standalone output.
  output: "standalone",
  // /scanner/ is the installed PWA start URL. Preserve it instead of redirecting to /scanner,
  // which is outside the deliberately slash-terminated manifest scope.
  skipTrailingSlashRedirect: true,
  // Workspace packages ship raw TS source, no build step (see docs/phase-8-plan.md mục 6) —
  // Next.js needs this to transpile them instead of treating them as pre-built node_modules.
  transpilePackages: ["@ticketflow/http-client", "@ticketflow/types", "@ticketflow/ui", "@ticketflow/web-auth"],
  // Multi-Zones (docs/phase-8-plan.md Commit 9, mục 1) — same shape as organizer-dashboard's
  // next.config.ts (see its comment for why `basePath` was tried and reverted). Pages, Auth.js,
  // and the validation BFF all physically live under src/app/scanner/*, so every public route
  // stays inside the zone prefix without a rewrite that changes the path Auth.js parses.
  assetPrefix: "/scanner-static",
  async rewrites() {
    return [
      // Public files are rooted at / in a standalone Next deployment. Keep the scanner's PWA
      // icon inside its mount when it is reached through the customer shell.
      { source: "/scanner-static/icon.svg", destination: "/icon.svg" },
    ];
  },
};

export default nextConfig;
