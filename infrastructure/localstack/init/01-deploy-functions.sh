#!/bin/bash
# Runs inside the LocalStack container (mounted read-only at
# /etc/localstack/init/ready.d/) once LocalStack itself reports ready. Deploys the
# TicketFlow.Functions probe functions (docs/phase-9-plan.md Phase 9 Commit 1).
#
# The zip this script deploys is built on the HOST before `docker compose up` —
# there's no .NET SDK in this container:
#   cd src/Functions/TicketFlow.Functions && dotnet lambda package -c Release -o function.zip
#
# Idempotent: safe to re-run on every LocalStack start (this hook fires on every container
# start, not just the first). LocalStack Community has no cross-restart persistence, so in
# practice every start IS a fresh deploy — idempotency here is about not erroring if a resource
# from a previous run happens to still exist rather than actually skipping work.
set -uo pipefail

ZIP_PATH="/functions/TicketFlow.Functions/function.zip"
ROLE_NAME="ticketflow-lambda-role"
ROLE_ARN="arn:aws:iam::000000000000:role/${ROLE_NAME}"
RULE_NAME="ticketflow-hello-timer-rule"
# Compose keeps the short Phase 9 probe cadence. Phase 11's external LocalStack fallback can
# defer it while the full kind stack is using the same Docker Desktop memory budget.
SCHEDULE_EXPRESSION="${HELLO_TIMER_SCHEDULE:-rate(1 minute)}"

# LocalStack 2026.6.3 returns no Lambda Configuration.State for this dotnet10 executor, so the
# AWS CLI `function-active-v2` waiter loops forever even after CreateFunction succeeds. Polling
# GetFunction is portable across the LocalStack versions used by this project; the later manual
# invoke/PDF smoke test is the meaningful runtime-readiness assertion.
wait_for_function() {
  local function_name="$1"
  until awslocal lambda get-function --function-name "$function_name" >/dev/null 2>&1; do
    sleep 2
  done
}

if [ ! -f "$ZIP_PATH" ]; then
  echo "[functions] $ZIP_PATH not found — run 'dotnet lambda package' in src/Functions/TicketFlow.Functions on the host first. Skipping function deploy."
  exit 0
fi

echo "[functions] deploying TicketFlow.Functions probe functions..."

awslocal iam create-role \
  --role-name "$ROLE_NAME" \
  --assume-role-policy-document '{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"Service":"lambda.amazonaws.com"},"Action":"sts:AssumeRole"}]}' \
  >/dev/null 2>&1 || echo "[functions] role $ROLE_NAME already exists, continuing"

# HelloFunction — plain HTTP-style invoke probe (Commit 1).
awslocal lambda create-function \
  --function-name HelloFunction \
  --runtime dotnet10 \
  --handler "TicketFlow.Functions::TicketFlow.Functions.HelloFunction::Handler" \
  --zip-file "fileb://${ZIP_PATH}" \
  --role "$ROLE_ARN" \
  --timeout 30 \
  >/dev/null 2>&1 || echo "[functions] HelloFunction already exists, continuing"

# HelloTimerFunction — EventBridge schedule probe (Commit 1).
#
# Real bug found while wiring this: a `void`-returning .NET handler crashes the dotnet10
# base image's bundled Runtime Interface Emulator with a Go-level SIGSEGV panic
# (rapidcore.Server.Invoke nil pointer dereference) on invoke. The crash puts the executor
# into a restart loop — LocalStack keeps recreating the execution container, which keeps
# re-invoking the handler, every ~1-2 seconds, indefinitely (looks identical to a busted
# EventBridge retry storm from the outside, but persisted even after the EventBridge rule and
# target were deleted, and reproduced from a single direct `lambda invoke` with no EventBridge
# involved at all — that isolated it to the handler's return type, not the trigger path).
# Fix: every handler in this project must return SOMETHING (a status string is enough), never
# `void`. Input parameter type (JsonElement vs a concrete type) was NOT a factor — isolated
# separately by testing each combination.
awslocal lambda create-function \
  --function-name HelloTimerFunction \
  --runtime dotnet10 \
  --handler "TicketFlow.Functions::TicketFlow.Functions.HelloTimerFunction::Handler" \
  --zip-file "fileb://${ZIP_PATH}" \
  --role "$ROLE_ARN" \
  --timeout 30 \
  >/dev/null 2>&1 || echo "[functions] HelloTimerFunction already exists, continuing"

wait_for_function HelloFunction
wait_for_function HelloTimerFunction

echo "[functions] wiring EventBridge rule for HelloTimerFunction (${SCHEDULE_EXPRESSION})..."
awslocal events put-rule \
  --name "$RULE_NAME" \
  --schedule-expression "$SCHEDULE_EXPRESSION" \
  >/dev/null

awslocal events put-targets \
  --rule "$RULE_NAME" \
  --targets "Id=1,Arn=arn:aws:lambda:us-east-1:000000000000:function:HelloTimerFunction" \
  >/dev/null

awslocal lambda add-permission \
  --function-name HelloTimerFunction \
  --statement-id "${RULE_NAME}-permission" \
  --action lambda:InvokeFunction \
  --principal events.amazonaws.com \
  --source-arn "arn:aws:events:us-east-1:000000000000:rule/${RULE_NAME}" \
  >/dev/null 2>&1 || echo "[functions] permission already exists, continuing"

echo "[functions] done — HelloFunction ready for manual invoke, HelloTimerFunction wired to a 1-minute EventBridge rule."
