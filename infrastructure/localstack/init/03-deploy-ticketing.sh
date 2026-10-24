#!/bin/bash
# Runs inside the LocalStack container after 01/02 (filename-sorted execution order for ready.d
# hooks). Deploys RenderTicketPdfFunction — PDF-vé rendering extracted out of Ticketing.Infrastructure
# (docs/phase-9-plan.md Phase 9 Commit 3).
#
# Same zip as 01/02 (`dotnet lambda package` in src/Functions/TicketFlow.Functions on the host) —
# all functions in this project ship from one assembly.
#
# Idempotent for the same reason 01/02 are: LocalStack Community has no cross-restart
# persistence, so every start is really a from-scratch deploy anyway.
set -uo pipefail

ZIP_PATH="/functions/TicketFlow.Functions/function.zip"
ROLE_ARN="arn:aws:iam::000000000000:role/ticketflow-lambda-role"
# Compose uses its Docker-network MinIO DNS name. Phase 11's external LocalStack fallback
# overrides this with host.docker.internal:<port>, where a temporary kubectl port-forward exposes
# the in-kind MinIO Service to the sibling Lambda executor container.
MINIO_ENDPOINT="${MINIO_ENDPOINT:-minio:9000}"

# See 01-deploy-functions.sh for the LocalStack dotnet10 waiter compatibility detail.
wait_for_function() {
  local function_name="$1"
  until awslocal lambda get-function --function-name "$function_name" >/dev/null 2>&1; do
    sleep 2
  done
}

if [ ! -f "$ZIP_PATH" ]; then
  echo "[ticketing] $ZIP_PATH not found — run 'dotnet lambda package' in src/Functions/TicketFlow.Functions on the host first. Skipping ticketing deploy."
  exit 0
fi

echo "[ticketing] deploying RenderTicketPdfFunction..."

# RenderTicketPdfFunction talks to MinIO, not LocalStack S3 (this is the ticket-PDF bucket from
# Phase 5, a different object store than the "posters" S3 bucket in 02-deploy-media.sh — see
# docs/phase-9-plan.md mục 3 for why the stack has two object stores). LocalStack doesn't inject
# MinIO credentials the way it auto-injects AWS ones for S3Client, so these come in explicitly as
# plain Lambda environment variables — same values Ticketing.Api's own appsettings.json use.
# With the default MINIO_ENDPOINT, "minio" resolves because LAMBDA_DOCKER_NETWORK joins the
# executor container onto this Compose project's default network. In Phase 11's K8s fallback,
# that executor lives outside the cluster and instead uses the explicit host-gateway endpoint above.
awslocal lambda create-function \
  --function-name RenderTicketPdfFunction \
  --runtime dotnet10 \
  --handler "TicketFlow.Functions::TicketFlow.Functions.RenderTicketPdfFunction::Handler" \
  --zip-file "fileb://${ZIP_PATH}" \
  --role "$ROLE_ARN" \
  --timeout 30 \
  --memory-size 512 \
  --environment "Variables={MINIO_ENDPOINT=${MINIO_ENDPOINT},MINIO_ACCESS_KEY=ticketflow,MINIO_SECRET_KEY=ticketflow,MINIO_BUCKET_NAME=tickets}" \
  >/dev/null 2>&1 || echo "[ticketing] RenderTicketPdfFunction already exists, continuing"

wait_for_function RenderTicketPdfFunction

echo "[ticketing] done — ticketing's IssueTicketsConsumer invokes RenderTicketPdfFunction by name via LambdaTicketRenderClient."
