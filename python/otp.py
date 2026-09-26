"""
Sending a one-time password over SMS.

Run: MESER10_API_KEY=... python3 otp.py 0501234567

Note what this does NOT do: it does not store the code or check it. That belongs
in your application, next to your session and your rate limiting, not in a
messaging client. What matters here is the sending.

One Python-specific trap, and it is the reason this file exists: the API hosts
sit behind Cloudflare with Browser Integrity Check on, and it reads the
User-Agent header. `urllib` sends `Python-urllib/3.x` by default, which comes
back as Cloudflare error 1010 and looks exactly like an API problem. The client
here always sends a User-Agent, and `requests` would have passed anyway. If you
write your own call with urllib, set the header yourself.
"""

import os
import secrets
import sys

from meser10_client import (
    ApiError,
    AuthenticationError,
    InvalidRequestError,
    Meser10Client,
    TransportError,
    sms_parts,
)


def main() -> int:
    key = os.environ.get("MESER10_API_KEY")
    if not key:
        print("Set MESER10_API_KEY first.", file=sys.stderr)
        return 1

    if len(sys.argv) < 2:
        print("Usage: python3 otp.py <phone>", file=sys.stderr)
        return 1

    phone = sys.argv[1]

    # Your application owns the code and its lifetime. This is an example value.
    code = f"{secrets.randbelow(900000) + 100000}"

    client = Meser10Client(key, user_agent="my-app/1.0")

    text = f"קוד האימות שלך הוא {code}. הקוד תקף ל-5 דקות."

    # Hebrew goes out as Unicode, so a single part holds 70 characters, not 160.
    print(f"This message will be billed as {sms_parts(text)} part(s).")

    try:
        client.send_sms(phone, text, "MyShop")
        print("Accepted for delivery.")
        return 0
    except InvalidRequestError as exc:
        # Refused before the network. Nothing sent, nothing spent.
        print(f"Bad call: {exc}", file=sys.stderr)
        return 2
    except AuthenticationError as exc:
        # Stop. Never loop here: repeated failures block this IP address for
        # hours, and the block is on the address, not the key.
        print(f"Key rejected. Stopping: {exc}", file=sys.stderr)
        return 3
    except ApiError as exc:
        # ErrorCode 4 on an SMS almost always means the sender identity is not
        # approved on the account yet.
        print(f"Gateway refused it (ErrorCode {exc.error_code}): {exc}", file=sys.stderr)
        return 4
    except TransportError as exc:
        print(f"Never got an answer: {exc}", file=sys.stderr)
        return 5


if __name__ == "__main__":
    raise SystemExit(main())

# One honest caveat before building a login on this: "accepted for delivery" is
# as much as the gateway will tell you. There is no per-message delivery status
# and no webhook, so you cannot confirm the code reached the handset. Offer a
# resend after a short wait, and a second route in, rather than assuming delivery.
