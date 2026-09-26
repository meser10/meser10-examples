"""
Sending one transactional email, in Hebrew, right to left.

Run: MESER10_API_KEY=... python3 receipt.py you@example.com
"""

import os
import sys

from meser10_client import Meser10Client, Meser10Error

# Nothing sets the direction for you. Hebrew mail sent without dir="rtl" gets
# left aligned by some clients, which is the usual cause of "the email looks
# broken" reports.
HTML = """<!doctype html>
<html dir="rtl" lang="he">
<body style="margin:0;padding:24px;font-family:Arial,Helvetica,sans-serif;background:#f6f7fb;">
  <div style="max-width:560px;margin:0 auto;background:#fff;border-radius:12px;padding:28px;">
    <h1 style="font-size:20px;margin:0 0 12px;">תודה על הרכישה</h1>
    <p style="font-size:15px;line-height:1.7;color:#3a3f52;margin:0 0 8px;">
      ההזמנה נרשמה והחשבונית מצורפת לחשבון שלך.
    </p>
    <p style="font-size:13px;color:#5a6070;margin:16px 0 0;">מספר הזמנה: 10482</p>
  </div>
</body>
</html>"""


def main() -> int:
    key = os.environ.get("MESER10_API_KEY")
    if not key or len(sys.argv) < 2:
        print("Usage: MESER10_API_KEY=... python3 receipt.py <email>", file=sys.stderr)
        return 1

    try:
        client = Meser10Client(key, user_agent="my-app/1.0")

        client.send_email(
            sys.argv[1],
            "החשבונית שלך",
            HTML,
            "MyShop",  # a display name only, the address is the account's
            "orders@example.com",  # required, whatever older docs show
        )

        print("Accepted for delivery.")
        return 0
    except Meser10Error as exc:
        print(f"{type(exc).__name__}: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

# Four things this function will not do, so check before routing a message
# through it: no From address (a display name only), no CC or BCC, no
# attachments, and no second recipient. The client refuses a comma separated
# list rather than letting the gateway silently take the first address.
