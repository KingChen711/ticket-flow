#!/bin/bash
# Runs inside the LocalStack container after 01-deploy-functions.sh (filename-sorted execution
# order for ready.d hooks). Deploys the poster upload/thumbnail/get Lambdas + S3 bucket + event
# notification wiring (docs/phase-9-plan.md Phase 9 Commit 2, DoD 1).
#
# Same zip as 01-deploy-functions.sh (`dotnet lambda package` in
# src/Functions/TicketFlow.Functions on the host) — all functions in this project ship from one
# assembly, each Lambda "function" just points at a different handler within it.
#
# Idempotent for the same reason 01-deploy-functions.sh is: LocalStack Community has no
# cross-restart persistence, so every start is really a from-scratch deploy anyway.
set -uo pipefail

ZIP_PATH="/functions/TicketFlow.Functions/function.zip"
ROLE_ARN="arn:aws:iam::000000000000:role/ticketflow-lambda-role"
BUCKET_NAME="posters"

# See 01-deploy-functions.sh: LocalStack's dotnet10 Function.State is absent in the version
# currently pinned by the floating local image, so `function-active-v2` is not a usable waiter.
wait_for_function() {
  local function_name="$1"
  until awslocal lambda get-function --function-name "$function_name" >/dev/null 2>&1; do
    sleep 2
  done
}

if [ ! -f "$ZIP_PATH" ]; then
  echo "[media] $ZIP_PATH not found — run 'dotnet lambda package' in src/Functions/TicketFlow.Functions on the host first. Skipping media deploy."
  exit 0
fi

echo "[media] deploying poster upload/thumbnail/get functions..."

# UploadPosterFunction and GetPosterFunction are invoked via the Lambda Invoke API by the gateway
# (MediaEndpoints.cs), NOT exposed as Function URLs — real finding while wiring this: LocalStack
# assigns a random subdomain per `create-function-url-config` call with no way to pin it, and it
# doesn't survive a restart either (no persistence). A URL the gateway's own static config would
# need to hardcode can't be random, so these are addressed by their stable function name instead.
for fn in UploadPosterFunction GetPosterFunction ThumbnailPosterFunction; do
  awslocal lambda create-function \
    --function-name "$fn" \
    --runtime dotnet10 \
    --handler "TicketFlow.Functions::TicketFlow.Functions.${fn}::Handler" \
    --zip-file "fileb://${ZIP_PATH}" \
    --role "$ROLE_ARN" \
    --timeout 30 \
    --memory-size 512 \
    >/dev/null 2>&1 || echo "[media] $fn already exists, continuing"
done

wait_for_function UploadPosterFunction
wait_for_function GetPosterFunction
wait_for_function ThumbnailPosterFunction

awslocal s3 mb "s3://${BUCKET_NAME}" >/dev/null 2>&1 || echo "[media] bucket $BUCKET_NAME already exists, continuing"

awslocal lambda add-permission \
  --function-name ThumbnailPosterFunction \
  --statement-id s3-posters-trigger \
  --action lambda:InvokeFunction \
  --principal s3.amazonaws.com \
  --source-arn "arn:aws:s3:::${BUCKET_NAME}" \
  >/dev/null 2>&1 || echo "[media] s3 invoke permission already exists, continuing"

# Filtered to "originals/" — ThumbnailPosterFunction writes its own output back into this same
# bucket under "thumbs/"; without this filter that write would re-trigger the same notification
# (confirmed there's no in-handler guard against this either — the filter is the single source
# of truth on purpose, see ThumbnailPosterFunction's doc comment).
cat > /tmp/posters-notification.json << 'EOF'
{
  "LambdaFunctionConfigurations": [
    {
      "Id": "thumbnail-on-upload",
      "LambdaFunctionArn": "arn:aws:lambda:us-east-1:000000000000:function:ThumbnailPosterFunction",
      "Events": ["s3:ObjectCreated:*"],
      "Filter": {
        "Key": {
          "FilterRules": [
            { "Name": "prefix", "Value": "originals/" }
          ]
        }
      }
    }
  ]
}
EOF

awslocal s3api put-bucket-notification-configuration \
  --bucket "$BUCKET_NAME" \
  --notification-configuration file:///tmp/posters-notification.json

echo "[media] done — POST through /media/events/{eventId}/posters and GET through /media/{key}, or invoke the functions directly by name."
