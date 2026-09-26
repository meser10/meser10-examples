"""
Subscribing someone, and reading their status back.

Run: MESER10_API_KEY=... python3 sync_contact.py you@example.com Newsletter
"""

import os
import sys

from meser10_client import ApiError, Meser10Client, Meser10Error


def main() -> int:
    key = os.environ.get("MESER10_API_KEY")
    if not key or len(sys.argv) < 2:
        print("Usage: MESER10_API_KEY=... python3 sync_contact.py <email> [list]", file=sys.stderr)
        return 1

    email = sys.argv[1]
    list_name = sys.argv[2] if len(sys.argv) > 2 else "Newsletter"

    client = Meser10Client(key, user_agent="my-app/1.0")

    try:
        # Check before writing. A successful change_contact_status proves nothing
        # about whether the contact existed, so this read is the only real check.
        before = client.status(email)
        print(f"Before: {before.name}")

        if before.is_unsubscribed:
            # Someone who removed themselves should not be quietly re-added.
            print("They unsubscribed. Not re-adding them.")
            return 0

        client.create_contact(
            list_name,
            EMail=email,
            FirstName="Dana",
            LastName="Levi",
            City="Tel Aviv",
        )

        after = client.status(email)
        print(f"After: {after.name}, mailable: {'yes' if after.is_mailable else 'no'}")
        return 0
    except ApiError as exc:
        if exc.error_code == "4":
            # The most common cause by far: the list name does not exist on this
            # account. The gateway names it in the message.
            print(f"The gateway refused a parameter. Usually the list: {exc.gateway_message}",
                  file=sys.stderr)
            return 4
        raise
    except Meser10Error as exc:
        print(f"{type(exc).__name__}: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
