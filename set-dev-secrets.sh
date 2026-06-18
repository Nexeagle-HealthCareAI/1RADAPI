#!/usr/bin/env bash
# Loads DEV secrets into .NET user-secrets (stored OUTSIDE the repo, never committed).
# Run AFTER you have ROTATED each secret on its provider — the old values are in
# git history and must be treated as compromised.
#
#   ./set-dev-secrets.sh
#
# User-secrets are picked up automatically only in the Development environment.
set -euo pipefail
PROJ="1RadAPI/1RadAPI.csproj"

# Replace each placeholder below with the NEW rotated value, then run this script.
BLOB_CONNECTION="DefaultEndpointsProtocol=https;AccountName=1radstorage;AccountKey=<NEW_ROTATED_KEY>;EndpointSuffix=core.windows.net"
JWT_SECRET="<NEW_JWT_SIGNING_SECRET>"
SMTP_APP_PASSWORD="<NEW_GMAIL_APP_PASSWORD>"
WHATSAPP_ACCESS_TOKEN="<NEW_META_WHATSAPP_TOKEN>"

dotnet user-secrets init --project "$PROJ" 2>/dev/null || true
dotnet user-secrets set "ConnectionStrings:AzureBlobStorage" "$BLOB_CONNECTION" --project "$PROJ"
dotnet user-secrets set "Jwt:Secret"                          "$JWT_SECRET"      --project "$PROJ"
dotnet user-secrets set "Smtp:AppPassword"                    "$SMTP_APP_PASSWORD" --project "$PROJ"
dotnet user-secrets set "WhatsApp:AccessToken"                "$WHATSAPP_ACCESS_TOKEN" --project "$PROJ"

echo "Done. Verify with: dotnet user-secrets list --project $PROJ"
