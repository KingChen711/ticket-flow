import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  // Phase 11 Commit 2 — Dockerfile copies only the standalone output.
  output: "standalone",
  // Workspace packages ship raw TS source, no build step (see docs/phase-8-plan.md mục 6) —
  // Next.js needs this to transpile them instead of treating them as pre-built node_modules.
  transpilePackages: ["@ticketflow/http-client", "@ticketflow/types", "@ticketflow/ui", "@ticketflow/web-auth"],
  // Multi-Zones (docs/phase-8-plan.md Commit 9, mục 1): customer-web is the shell and rewrites
  // /organizer/* here. Pages and the Auth.js route physically live under src/app/organizer/*
  // (manual prefix, not Next's `basePath`). Auth.js uses its own explicit
  // /organizer/api/auth basePath, so the public callback and the physical route agree without
  // Next stripping a framework-level basePath before Auth.js parses the request.
  // assetPrefix only moves /_next/static/* under /organizer-static so it can't collide with the
  // shell's or gate-scanner's own /_next/* once proxied under the same origin.
  assetPrefix: "/organizer-static",
};

export default nextConfig;
