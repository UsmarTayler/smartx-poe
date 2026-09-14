#!/bin/sh
# Writes the client's appsettings.json from the container's environment before nginx starts.
#
# A published WebAssembly app reads its configuration in the browser, from a static file, so the usual
# ASP.NET Core environment variables cannot reach it. Generating the file at start-up means one image can
# point at any API without being rebuilt.
set -eu

config=/usr/share/nginx/html/appsettings.json

cat > "$config" <<EOF
{
  "ApiBaseAddress": "${API_BASE_ADDRESS:-/}",
  "TrustFeed": {
    "PollIntervalSeconds": ${TRUST_POLL_INTERVAL_SECONDS:-3}
  },
  "Logging": {
    "LogLevel": {
      "Default": "Warning"
    }
  }
}
EOF

echo "smartx-client: ApiBaseAddress is ${API_BASE_ADDRESS:-/}"
