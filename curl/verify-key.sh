#!/usr/bin/env bash
#
# Is this key accepted? Creates nothing, sends nothing, spends nothing.
#
#   MESER10_API_KEY=... ./verify-key.sh
#
# Run this instead of retrying a real call when you suspect a key problem.
# Retrying a failing call is what gets an IP address blocked for hours.

set -euo pipefail

: "${MESER10_API_KEY:?Set MESER10_API_KEY first}"

ENDPOINT="https://heb.mesereser.com/Services/JsonServices.aspx"

# GetContactStatus is the only read on the gateway, and the address has to travel
# on the query string: every JSON body spelling answers "email parameter is
# empty". This address is on no account, so a valid key answers ErrorCode 0 with
# StatusID 0 and nothing is created.
RESPONSE=$(curl -sS "${ENDPOINT}?f=GetContactStatus&email=nobody.probe@example.invalid" \
  -H "ApiKey: ${MESER10_API_KEY}" \
  -H "User-Agent: meser10-curl-example/1.0")

echo "Raw answer: ${RESPONSE}"

case "${RESPONSE}" in
  *'"ErrorCode":0'*|*'"ErrorCode": 0'*|*'"ErrorCode":"0"'*)
    echo "The key is accepted."
    ;;
  *'"ErrorCode":1'*|*'"ErrorCode": 1'*|*'"ErrorCode":"1"'*)
    echo "The key was rejected. Reissue it in the Meser 10 interface; do not retry this call." >&2
    exit 1
    ;;
  *)
    echo "Unexpected answer. If it is HTML, Cloudflare refused the request over the User-Agent." >&2
    exit 2
    ;;
esac
