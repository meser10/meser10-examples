#!/usr/bin/env bash
#
# Sending a one-time password with nothing but curl.
#
#   MESER10_API_KEY=... ./otp.sh 0501234567
#
# Useful as the smallest possible reference, and as the thing to run when a
# library is behaving oddly and you want to see the raw answer.

set -euo pipefail

: "${MESER10_API_KEY:?Set MESER10_API_KEY first}"
PHONE="${1:?Usage: ./otp.sh <phone>}"

ENDPOINT="https://heb.mesereser.com/Services/JsonServices.aspx"

# Your application owns the code and its lifetime. This is an example value.
CODE=$(( RANDOM % 900000 + 100000 ))

# Two headers matter. ApiKey carries the account key, and it goes in a header
# rather than the query string, where it would land in server logs and browser
# history. User-Agent matters because the hosts sit behind Cloudflare with
# Browser Integrity Check on: curl's own signature passes, but a default library
# signature such as Python-urllib/3.x is refused with Cloudflare error 1010.
RESPONSE=$(curl -sS -X POST "${ENDPOINT}?f=SendSingleSmsMessage" \
  -H "ApiKey: ${MESER10_API_KEY}" \
  -H "Content-Type: application/json; charset=utf-8" \
  -H "User-Agent: meser10-curl-example/1.0" \
  --data-binary @- <<JSON
{
  "ToPhone": "${PHONE}",
  "MessageBody": "קוד האימות שלך הוא ${CODE}. הקוד תקף ל-5 דקות.",
  "FromName": "MyShop"
}
JSON
)

echo "Raw answer: ${RESPONSE}"

# HTTP 200 comes back for every outcome, including a rejected key, so the status
# code proves nothing. ErrorCode is the only reliable indicator, and it arrives
# as a number on some functions and as a string on others, hence the loose match.
CODE_FIELD=$(printf '%s' "${RESPONSE}" | sed -n 's/.*"ErrorCode"[[:space:]]*:[[:space:]]*"\{0,1\}\([0-9]\{1,\}\).*/\1/p')

case "${CODE_FIELD}" in
  0)
    echo "Accepted for delivery."
    ;;
  1)
    # Stop here. Repeated authentication failures block this IP address for
    # several hours, and the block is on the address, not the key, so reissuing
    # the key and running this again makes it worse.
    echo "The key was rejected. Do NOT retry: repeated failures block this IP for hours." >&2
    exit 3
    ;;
  4)
    # On an SMS this almost always means the sender name is not approved yet.
    echo "A parameter was refused. On an SMS this is usually an unapproved sender name." >&2
    exit 4
    ;;
  "")
    echo "The answer was not JSON. If it is HTML, Cloudflare refused the request." >&2
    exit 5
    ;;
  *)
    echo "The gateway refused it with ErrorCode ${CODE_FIELD}." >&2
    exit 6
    ;;
esac

# There is no per-message delivery status and no webhook, and MessageID comes
# back as 0, so "accepted for delivery" is as much as you get. A one-time
# password cannot be confirmed as having reached the handset.
