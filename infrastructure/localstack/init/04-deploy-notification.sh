#!/bin/bash
# Runs inside the LocalStack container after 01/02/03 (filename-sorted execution order for
# ready.d hooks). Deploys EventReminderTimer — the cron trigger for DoD 2
# (docs/phase-9-plan.md Phase 9 Commit 4, mục 5).
#
# Same zip as 01/02/03 (`dotnet lambda package` in src/Functions/TicketFlow.Functions on the
# host) — all functions in this project ship from one assembly.
#
# Idempotent for the same reason 01/02/03 are: LocalStack Community has no cross-restart
# persistence, so every start is really a from-scratch deploy anyway.
set -uo pipefail

ZIP_PATH="/functions/TicketFlow.Functions/function.zip"
ROLE_ARN="arn:aws:iam::000000000000:role/ticketflow-lambda-role"
RULE_NAME="ticketflow-event-reminder-rule"

# Configurable per mục 5/6: "24h prod / 1 phút dev". This is the ONLY thing that changes between
# environments — the reminder WINDOW (Reminder:WindowHours in notification-service's appsettings)
# stays the same 24h business rule regardless of how often the tick fires. Override by setting
# EVENT_REMINDER_SCHEDULE before `docker compose up` for a prod-like cadence, e.g.
# EVENT_REMINDER_SCHEDULE="rate(24 hours)".
SCHEDULE_EXPRESSION="${EVENT_REMINDER_SCHEDULE:-rate(1 minute)}"

# See 01-deploy-functions.sh for the LocalStack dotnet10 waiter compatibility detail.
wait_for_function() {
  local function_name="$1"
  until awslocal lambda get-function --function-name "$function_name" >/dev/null 2>&1; do
    sleep 2
  done
}

if [ ! -f "$ZIP_PATH" ]; then
  echo "[notification] $ZIP_PATH not found — run 'dotnet lambda package' in src/Functions/TicketFlow.Functions on the host first. Skipping notification deploy."
  exit 0
fi

echo "[notification] deploying EventReminderTimerFunction..."

# EventReminderTimerFunction publishes onto RabbitMQ via a standalone MassTransit bus (no ASP.NET
# Core host inside a bare Lambda class library) — env vars use the double-underscore convention
# ASP.NET Core's own EnvironmentVariablesConfigurationProvider expects for nested config keys
# (RabbitMq__Host -> configuration["RabbitMq:Host"]), same values every other service's RabbitMq
# section already uses. RabbitMQ reachability from this executor was already confirmed in Commit 1
# (nc -zv rabbitmq 5672).
awslocal lambda create-function \
  --function-name EventReminderTimerFunction \
  --runtime dotnet10 \
  --handler "TicketFlow.Functions::TicketFlow.Functions.EventReminderTimerFunction::Handler" \
  --zip-file "fileb://${ZIP_PATH}" \
  --role "$ROLE_ARN" \
  --timeout 30 \
  --memory-size 512 \
  --environment "Variables={RabbitMq__Host=rabbitmq,RabbitMq__Username=ticketflow,RabbitMq__Password=ticketflow}" \
  >/dev/null 2>&1 || echo "[notification] EventReminderTimerFunction already exists, continuing"

wait_for_function EventReminderTimerFunction

echo "[notification] wiring EventBridge rule for EventReminderTimerFunction (schedule: ${SCHEDULE_EXPRESSION})..."
awslocal events put-rule \
  --name "$RULE_NAME" \
  --schedule-expression "$SCHEDULE_EXPRESSION" \
  >/dev/null

awslocal events put-targets \
  --rule "$RULE_NAME" \
  --targets "Id=1,Arn=arn:aws:lambda:us-east-1:000000000000:function:EventReminderTimerFunction" \
  >/dev/null

awslocal lambda add-permission \
  --function-name EventReminderTimerFunction \
  --statement-id "${RULE_NAME}-permission" \
  --action lambda:InvokeFunction \
  --principal events.amazonaws.com \
  --source-arn "arn:aws:events:us-east-1:000000000000:rule/${RULE_NAME}" \
  >/dev/null 2>&1 || echo "[notification] permission already exists, continuing"

echo "[notification] done — EventReminderTimerFunction ticking on schedule '${SCHEDULE_EXPRESSION}', publishing SendUpcomingEventReminders to notification-service."
