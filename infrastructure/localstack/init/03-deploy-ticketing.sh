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
# "minio" resolves here because LAMBDA_DOCKER_NETWORK joins the executor container onto
# ticketflow-network (confirmed reachable in Commit 1's risk #1 verification: nc -zv minio 9000).
awslocal lambda create-function \
  --function-name RenderTicketPdfFunction \
  --runtime dotnet10 \
  --handler "TicketFlow.Functions::TicketFlow.Functions.RenderTicketPdfFunction::Handler" \
  --zip-file "fileb://${ZIP_PATH}" \
  --role "$ROLE_ARN" \
  --timeout 30 \
  --memory-size 512 \
  --environment "Variables={MINIO_ENDPOINT=minio:9000,MINIO_ACCESS_KEY=ticketflow,MINIO_SECRET_KEY=ticketflow,MINIO_BUCKET_NAME=tickets}" \
  >/dev/null 2>&1 || echo "[ticketing] RenderTicketPdfFunction already exists, continuing"

awslocal lambda wait function-active-v2 --function-name RenderTicketPdfFunction

echo "[ticketing] done — ticketing's IssueTicketsConsumer invokes RenderTicketPdfFunction by name via LambdaTicketRenderClient."
