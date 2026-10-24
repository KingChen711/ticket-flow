import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  // Phase 11 Commit 2 — Dockerfile copies only the standalone output.
  output: "standalone",
  // Workspace packages ship raw TS source, no build step (see docs/phase-8-plan.md mục 6) —
  // Next.js needs this to transpile them instead of treating them as pre-built node_modules.
  transpilePackages: ["@ticketflow/types"],
  // Multi-Zones (docs/phase-8-plan.md Commit 9, mục 1) — same shape as organizer-dashboard's
  // next.config.ts (see its comment for why `basePath` was tried and reverted). Pages live under
  // src/app/scanner/*; /api/auth/* and /api/validate stay unprefixed at the app root.
  assetPrefix: "/scanner-static",
};

export default nextConfig;
