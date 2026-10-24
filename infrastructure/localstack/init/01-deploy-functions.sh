#!/bin/bash
# Runs inside the LocalStack container (mounted read-only at /etc/localstack/init/ready.d/) once
# LocalStack itself reports ready, before the other numbered scripts. Creates the shared IAM role
# that 02-04 deploy their functions under.
#
# Every handler in TicketFlow.Functions must return a value, never `void`: a void .NET handler
# crashes the dotnet10 base image's Runtime Interface Emulator (Go nil pointer panic) on invoke,
# and LocalStack then restarts and re-invokes it in a loop (docs/phase-9-plan.md, Commit 1).
#
# Idempotent: this hook fires on every container start; an existing role is not an error.
set -uo pipefail

ROLE_NAME="ticketflow-lambda-role"

awslocal iam create-role \
  --role-name "$ROLE_NAME" \
  --assume-role-policy-document '{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"Service":"lambda.amazonaws.com"},"Action":"sts:AssumeRole"}]}' \
  >/dev/null 2>&1 || echo "[functions] role $ROLE_NAME already exists, continuing"

echo "[functions] IAM role ready."
