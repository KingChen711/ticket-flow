# Phase 11 Commit 2 — pnpm workspace monorepo build (no Turborepo, see docs/phase-8-plan.md mục
# 6), `output: "standalone"` (next.config.ts) traces only the deps this app actually needs
# (including the workspace-linked @ticketflow/types and @ticketflow/ui, which ship raw TS source
# with no build step of their own — transpilePackages handles them at Next's own build time).
FROM node:22-alpine AS base
WORKDIR /repo
RUN corepack enable

FROM base AS deps
COPY package.json pnpm-workspace.yaml pnpm-lock.yaml ./
COPY apps/customer-web/package.json apps/customer-web/
COPY apps/organizer-dashboard/package.json apps/organizer-dashboard/
COPY apps/gate-scanner/package.json apps/gate-scanner/
COPY packages/types/package.json packages/types/
COPY packages/ui/package.json packages/ui/
RUN pnpm install --frozen-lockfile

FROM deps AS build
# next.config.ts is evaluated during `next build`, so the Multi-Zones destinations and the
# zone destinations must be available *now*, not only as runtime pod env vars. The public
# SignalR URL is deliberately not a NEXT_PUBLIC build arg: the server reads PUBLIC_GATEWAY_URL
# at request time and passes it to the client component, so one immutable image can move between
# environments without baking a developer's localhost into browser JavaScript.
ARG ORGANIZER_ZONE_URL=http://organizer-dashboard:3001
ARG SCANNER_ZONE_URL=http://gate-scanner:3002
ENV ORGANIZER_ZONE_URL=$ORGANIZER_ZONE_URL \
    SCANNER_ZONE_URL=$SCANNER_ZONE_URL
COPY . .
RUN pnpm --filter customer-web build

FROM base AS runtime
ENV NODE_ENV=production
COPY --from=build /repo/apps/customer-web/.next/standalone ./
COPY --from=build /repo/apps/customer-web/.next/static ./apps/customer-web/.next/static
COPY --from=build /repo/apps/customer-web/public ./apps/customer-web/public
EXPOSE 3000
CMD ["node", "apps/customer-web/server.js"]
