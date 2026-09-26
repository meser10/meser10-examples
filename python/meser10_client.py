"""
A compact client for the Meser 10 JSON gateway.

Single file on purpose, so you can copy it into a project without adding a
dependency. It needs `requests`, or you can pass any callable with the same
shape as ``requests.request``.

Three behaviours of the gateway are handled here, because each one has cost
somebody a day:

  * Every call answers HTTP 200, including a rejected key. Success lives in
    ``ErrorCode``, which is an int on some functions and a str on others.
  * Repeated authentication failures block the CALLING IP ADDRESS for several
    hours, and the block is on the address, not the key. So there is no retry
    logic here at all, and after one authentication failure the client latches
    shut and refuses to call again, even if your code loops.
  * The hosts sit behind Cloudflare with Browser Integrity Check on, which reads
    the User-Agent header. ``Python-urllib/3.x`` is blocked and answers
    Cloudflare error 1010. A User-Agent is always sent here.
"""

from __future__ import annotations

import json
import re
from typing import Any, Callable, Dict, Optional

__all__ = [
    "Meser10Client",
    "Meser10Error",
    "InvalidRequestError",
    "AuthenticationError",
    "ApiError",
    "TransportError",
    "ContactStatus",
    "assert_sender_is_well_formed",
    "sms_parts",
]

DEFAULT_BASE_URL = "https://heb.mesereser.com/Services/JsonServices.aspx"

# An address no account has, for probing a key without writing anything.
PROBE_ADDRESS = "nobody.probe@example.invalid"

VERSION = "1.0.0"

CONTACT_FIELDS = (
    "EMail",
    "PhoneNo",
    "FirstName",
    "LastName",
    "Address",
    "City",
    "Zipcode",
    "CustomField1",
    "CustomField2",
    "CustomField3",
    "CustomField4",
    "CustomField5",
)

STATUS_VALUES = ("Active", "Unsubscribed", "Bounced")

_EMAIL = re.compile(r"^[^\s@]+@[^\s@]+\.[^\s@]+$")

_GSM_7BIT = re.compile(
    r"^[A-Za-z0-9 \r\n@£$¥èéùìòÇØøÅåÆæßÉ!\"#¤%&'()*+,\-./:;<=>?_¡ÄÖÑÜ§¿äöñüà^{}\[\]~|€]*$"
)


class Meser10Error(Exception):
    """Base class, so one except clause covers everything this module raises."""


class InvalidRequestError(Meser10Error):
    """Refused here, before the network. Nothing sent, nothing spent."""


class AuthenticationError(Meser10Error):
    """
    ErrorCode 1: the key was rejected.

    Catch this to stop, log and alert. Never to retry.
    """


class TransportError(Meser10Error):
    """No readable answer: network, timeout, or Cloudflare."""


class ApiError(Meser10Error):
    """Any other ErrorCode. ``gateway_message`` arrives in Hebrew on most failures."""

    def __init__(self, error_code: str, gateway_message: str, function: str, message: str) -> None:
        super().__init__(message)
        self.error_code = error_code
        self.gateway_message = gateway_message
        self.function = function

    @property
    def is_transient(self) -> bool:
        """True when a single retry is reasonable. Never true for a key failure."""
        return self.error_code == "3"

    @classmethod
    def from_code(cls, code: str, gateway_message: str, function: str) -> "ApiError":
        advice = {
            "3": (
                "An application error, either on the gateway or a payload it could not process at "
                "all. Safe to try once more; if it persists, send support the tracking id in the "
                "gateway message."
            ),
            "4": (
                "A parameter was missing or invalid. On create_contact this most often means the "
                "named list does not exist on this account, and on an SMS it most often means the "
                "sender identity is not approved yet. The gateway message names which."
            ),
            "6": "The gateway does not know this function name.",
        }.get(code, "An error code this module does not recognise.")

        return cls(
            code,
            gateway_message,
            function,
            f"{function} failed with ErrorCode {code}. {advice} Gateway said: {gateway_message}",
        )


class ContactStatus:
    """
    A contact's state on the account.

    Two ids mean active: a contact created through the API is 10, and one moved
    back to Active after a bounce or an unsubscribe is 30. Code that checks for
    10 alone silently drops every reactivated contact, so use ``is_mailable``.
    """

    NOT_FOUND = 0
    ACTIVE = 10
    REACTIVATED = 30
    BOUNCED = 40
    UNSUBSCRIBED = 50

    _NAMES = {
        NOT_FOUND: "not_found",
        ACTIVE: "active",
        REACTIVATED: "active_reactivated",
        BOUNCED: "bounced",
        UNSUBSCRIBED: "unsubscribed",
    }

    def __init__(self, status_id: int, label: str = "") -> None:
        self.id = status_id
        #: The gateway's own label, in Hebrew for the real states. Display only.
        self.label = label

    @classmethod
    def from_response(cls, body: Dict[str, Any]) -> "ContactStatus":
        return cls(int(body.get("StatusID", cls.NOT_FOUND)), str(body.get("Status", "")))

    @property
    def exists(self) -> bool:
        return self.id != self.NOT_FOUND

    @property
    def is_mailable(self) -> bool:
        return self.id in (self.ACTIVE, self.REACTIVATED)

    @property
    def is_bounced(self) -> bool:
        return self.id == self.BOUNCED

    @property
    def is_unsubscribed(self) -> bool:
        return self.id == self.UNSUBSCRIBED

    @property
    def name(self) -> str:
        """A stable English name for logs, independent of the gateway's label."""
        return self._NAMES.get(self.id, f"unknown_{self.id}")

    def __repr__(self) -> str:
        return f"ContactStatus(id={self.id}, name={self.name!r})"


def assert_sender_is_well_formed(from_name: str) -> None:
    """
    Checks a sender identity against the network rules before a request is spent.

    Two forms are accepted. An alphanumeric sender name of up to 11 characters,
    Latin letters, digits and spaces only, containing at least one letter. Or a
    number, in local or E.164 form.

    The 11-character limit is a GSM constraint on alphanumeric sender IDs, not a
    Meser 10 one, and support varies by destination: alphanumeric sender IDs are
    not available in the United States or Canada, where a number is used instead.

    This checks the shape only. Whether the identity is approved on the account
    is decided by the gateway.
    """
    value = (from_name or "").strip()

    if not value:
        raise InvalidRequestError("No sender identity given.")

    if re.fullmatch(r"\+?[0-9]{6,19}", value):
        return

    if re.search(r"[^A-Za-z0-9 ]", value):
        raise InvalidRequestError(
            f"The sender name {value!r} contains characters the mobile networks reject. An "
            "alphanumeric sender name may hold only Latin letters, digits and spaces, so Hebrew "
            "text cannot be used as a sender name. Use an approved number instead."
        )

    if len(value) > 11:
        raise InvalidRequestError(
            f"The sender name {value!r} is {len(value)} characters. The limit is 11, and a longer "
            "name is not truncated, the call is simply rejected."
        )

    if not re.search(r"[A-Za-z]", value):
        raise InvalidRequestError(
            f"The sender name {value!r} has no letter in it. A digits-only value is rejected as an "
            "alphanumeric sender name. If you meant a phone number, give the full number."
        )


def sms_parts(text: str) -> int:
    """
    How many parts this text will be billed as.

    Any character outside the GSM 7-bit set, which includes every Hebrew letter,
    pushes the whole message to Unicode: 70 characters for a single part, 67 per
    part once it is concatenated.
    """
    length = len(text)
    if length == 0:
        return 0

    unicode_needed = _GSM_7BIT.fullmatch(text) is None
    single = 70 if unicode_needed else 160
    multi = 67 if unicode_needed else 153

    return 1 if length <= single else -(-length // multi)


class Meser10Client:
    """
    Five functions: one SMS, one email, add or update a contact, change a
    contact's status, read a contact's status.

    Campaigns, mailing lists, groups, reporting and attachments are on the SOAP
    service, not on this gateway.
    """

    def __init__(
        self,
        api_key: str,
        user_agent: str = f"meser10-python/{VERSION}",
        base_url: str = DEFAULT_BASE_URL,
        timeout: int = 20,
        requester: Optional[Callable[..., Any]] = None,
    ) -> None:
        key = (api_key or "").strip()
        if not key:
            raise InvalidRequestError("The API key is empty.")

        if requester is None:
            try:
                import requests
            except ImportError as exc:  # pragma: no cover
                raise TransportError(
                    "requests is not installed. pip install requests, or pass a requester."
                ) from exc
            requester = requests.request

        self._api_key = key
        self._user_agent = user_agent
        self._base_url = base_url
        self._timeout = timeout
        self._request = requester

        # Set once an authentication failure has been seen, and never cleared.
        self._locked: Optional[AuthenticationError] = None

    # ------------------------------------------------------------- messaging

    def send_sms(self, to: str, body: str, from_name: str) -> Dict[str, Any]:
        """
        One SMS, one recipient, sent immediately.

        MessageID comes back as 0, and the gateway has no way to read a
        message's delivery state, so keep your own identifier if you need to
        trace a send.
        """
        phone = (to or "").strip()
        text = (body or "").strip()

        if not phone:
            raise InvalidRequestError("No recipient. One SMS goes to one number.")
        if not text:
            raise InvalidRequestError("The message body is empty.")

        assert_sender_is_well_formed(from_name)

        return self._post(
            "SendSingleSmsMessage",
            {"ToPhone": phone, "MessageBody": text, "FromName": from_name.strip()},
        )

    def send_email(
        self, to: str, subject: str, html: str, from_name: str, reply_to: str
    ) -> Dict[str, Any]:
        """
        One email, one recipient, sent immediately.

        The gateway accepts no From address (a display name only), no CC, no
        BCC, no attachments and no second recipient, and there is deliberately
        no way to pass any of them here.
        """
        for what, value in (
            ("recipient", to),
            ("subject", subject),
            ("body", html),
            ("sender name", from_name),
        ):
            if not (value or "").strip():
                raise InvalidRequestError(f"The {what} is empty.")

        address = (reply_to or "").strip()
        if not address:
            raise InvalidRequestError(
                "reply_to is required by the gateway. Without it the call fails with an "
                "application error."
            )
        if not _EMAIL.fullmatch(address):
            raise InvalidRequestError(f"reply_to is not a valid address: {address}")
        if "," in to or ";" in to:
            raise InvalidRequestError(
                "One recipient per call. Send a separate message per address rather than a list."
            )

        return self._post(
            "SendSingleEMailMessage",
            {
                "ToEMail": to.strip(),
                "Subject": subject,
                "Body": html,
                "FromName": from_name.strip(),
                "ReplyToEMail": address,
            },
        )

    # -------------------------------------------------------------- contacts

    def create_contact(self, list_name: str, **fields: str) -> Dict[str, Any]:
        """
        Adds a contact to a named list, or updates them if they already exist.

        The list is not created for you. Unknown keys are refused rather than
        dropped silently, because ``email`` instead of ``EMail`` is the easiest
        mistake to make against this gateway and it fails looking like nothing
        happened.
        """
        name = (list_name or "").strip()
        if not name:
            raise InvalidRequestError("No list name. The gateway will not guess one.")

        payload: Dict[str, str] = {"ContactListName": name}

        for key, value in fields.items():
            if key not in CONTACT_FIELDS:
                raise InvalidRequestError(
                    f"Unknown contact field {key!r}. The gateway accepts: "
                    f"{', '.join(CONTACT_FIELDS)}. Note the capitalisation of EMail and PhoneNo."
                )
            trimmed = str(value or "").strip()
            if trimmed:
                payload[key] = trimmed

        if "EMail" not in payload and "PhoneNo" not in payload:
            raise InvalidRequestError("A contact needs at least an EMail or a PhoneNo.")

        return self._post("CreateContact", payload)

    def change_contact_status(self, email_or_phone: str, status: str) -> Dict[str, Any]:
        """
        Moves a contact to Active, Unsubscribed or Bounced.

        An address which is not on the account also answers success, so this
        cannot tell you whether the contact existed. Use ``status()`` for that.
        """
        who = (email_or_phone or "").strip()
        if not who:
            raise InvalidRequestError("No contact given.")
        if status not in STATUS_VALUES:
            raise InvalidRequestError(
                f"Status must be one of {', '.join(STATUS_VALUES)}, got {status!r}."
            )

        key = "EMail" if _EMAIL.fullmatch(who) else "PhoneNo"

        return self._post("ChangeContactStatus", {key: who, "Status": status})

    def status(self, email: str) -> ContactStatus:
        """
        Reads a contact's current status.

        Only an email address works: the gateway does not read a phone
        parameter, and the address has to travel on the query string.
        """
        address = (email or "").strip()
        if not address:
            raise InvalidRequestError("No address given.")

        return ContactStatus.from_response(self._get("GetContactStatus", {"email": address}))

    def verify_key(self) -> bool:
        """
        Is this key accepted? Creates nothing, sends nothing, spends nothing.

        Use this instead of retrying a real call when you suspect a key problem.
        Retrying is what gets an IP address blocked.
        """
        try:
            self._get("GetContactStatus", {"email": PROBE_ADDRESS})
            return True
        except AuthenticationError:
            return False

    # -------------------------------------------------------------- plumbing

    def _post(self, function: str, payload: Dict[str, Any]) -> Dict[str, Any]:
        return self._call("POST", function, {}, json.dumps(payload, ensure_ascii=False))

    def _get(self, function: str, query: Dict[str, str]) -> Dict[str, Any]:
        return self._call("GET", function, query, None)

    def _call(
        self, method: str, function: str, query: Dict[str, str], body: Optional[str]
    ) -> Dict[str, Any]:
        if self._locked is not None:
            # Deliberate: a caller that loops on failure must not reach the
            # network again, because that is what blocks the IP address.
            raise self._locked

        params = {"f": function, **query}

        headers = {
            "ApiKey": self._api_key,
            # Without this, Cloudflare answers error 1010 to Python-urllib and
            # the failure looks like an API problem rather than a header problem.
            "User-Agent": self._user_agent,
            "Accept": "application/json",
        }
        if body is not None:
            headers["Content-Type"] = "application/json; charset=utf-8"

        try:
            response = self._request(
                method,
                self._base_url,
                params=params,
                headers=headers,
                data=body.encode("utf-8") if body is not None else None,
                timeout=self._timeout,
                allow_redirects=False,
            )
        except Exception as exc:  # network, DNS, timeout
            raise TransportError(f"The request did not complete: {exc}") from exc

        status_code = int(getattr(response, "status_code", 0))
        text = response.text

        try:
            decoded = json.loads(text)
        except ValueError as exc:
            if status_code == 403 or "cloudflare" in text.lower():
                raise TransportError(
                    "Cloudflare refused the request, which happens when the User-Agent is a "
                    "default library signature. This client sets one, so check whether a proxy is "
                    f"replacing it. HTTP status was {status_code}."
                ) from exc
            raise TransportError(
                f"The gateway answered something that is not JSON (HTTP {status_code}): "
                f"{text.strip()[:200]}"
            ) from exc

        if not isinstance(decoded, dict):
            raise TransportError(f"The gateway answered JSON that is not an object: {text[:200]}")

        # ErrorCode is an int on some functions and a str on others.
        code = str(decoded.get("ErrorCode", ""))
        result = str(decoded.get("Result", ""))

        if code == "0":
            return decoded

        if code == "1":
            self._locked = AuthenticationError(
                "The API key was rejected. Do not retry: repeated authentication failures block "
                "the calling IP address for several hours, and the block is on the address, not "
                "the key, so reissuing the key and trying again makes it worse. Gateway said: "
                f"{result}"
            )
            raise self._locked

        raise ApiError.from_code(code, result, function)
