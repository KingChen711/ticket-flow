# Phase 11 Commit 2 — same shape as apps/customer-web/Dockerfile (see its comment).
FROM node:22-alpine AS base
WORKDIR /repo
RUN corepack enable

FROM base AS deps
COPY package.json pnpm-workspace.yaml pnpm-lock.yaml ./
COPY apps/customer-web/package.json apps/customer-web/
COPY apps/organizer-dashboard/package.json apps/organizer-dashboard/
COPY apps/gate-scanner/package.json apps/gate-scanner/
COPY packages/http-client/package.json packages/http-client/
COPY packages/types/package.json packages/types/
COPY packages/web-auth/package.json packages/web-auth/
RUN pnpm install --frozen-lockfile

FROM deps AS build
COPY . .
RUN pnpm --filter gate-scanner build

FROM base AS runtime
ENV NODE_ENV=production
COPY --from=build /repo/apps/gate-scanner/.next/standalone ./
COPY --from=build /repo/apps/gate-scanner/.next/static ./apps/gate-scanner/.next/static
COPY --from=build /repo/apps/gate-scanner/public ./apps/gate-scanner/public
EXPOSE 3002
CMD ["node", "apps/gate-scanner/server.js"]
