# Phase 11 Commit 2 — same shape as apps/customer-web/Dockerfile (see its comment).
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
COPY . .
RUN pnpm --filter organizer-dashboard build

FROM base AS runtime
ENV NODE_ENV=production
COPY --from=build /repo/apps/organizer-dashboard/.next/standalone ./
COPY --from=build /repo/apps/organizer-dashboard/.next/static ./apps/organizer-dashboard/.next/static
COPY --from=build /repo/apps/organizer-dashboard/public ./apps/organizer-dashboard/public
EXPOSE 3001
CMD ["node", "apps/organizer-dashboard/server.js"]
