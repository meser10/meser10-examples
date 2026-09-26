"""
Tests for meser10_client. Nothing here touches the network or sends a message.

Run: python3 test_client.py
"""

import json
import sys

from meser10_client import (
    ApiError,
    AuthenticationError,
    ContactStatus,
    InvalidRequestError,
    Meser10Client,
    TransportError,
    assert_sender_is_well_formed,
    sms_parts,
)


class FakeResponse:
    def __init__(self, status_code, text):
        self.status_code = status_code
        self.text = text


class FakeRequester:
    """Records what was sent and answers with whatever the test queued."""

    def __init__(self):
        self.queue = []
        self.calls = []

    def queue_json(self, payload, status=200):
        self.queue.append(FakeResponse(status, json.dumps(payload, ensure_ascii=False)))
        return self

    def queue_raw(self, status, text):
        self.queue.append(FakeResponse(status, text))
        return self

    def __call__(self, method, url, **kwargs):
        self.calls.append({"method": method, "url": url, **kwargs})
        if not self.queue:
            raise AssertionError("The fake requester was called more times than the test queued.")
        return self.queue.pop(0)

    @property
    def last(self):
        return self.calls[-1]

    def last_body(self):
        body = self.last.get("data")
        return json.loads(body.decode("utf-8")) if body else None


def client(fake):
    return Meser10Client("test-key", requester=fake)


PASSED = 0
FAILED = []


def test(name):
    def decorate(fn):
        global PASSED
        try:
            fn()
            PASSED += 1
            print(f"  ok    {name}")
        except Exception as exc:  # noqa: BLE001
            FAILED.append(name)
            print(f"  FAIL  {name}\n        {type(exc).__name__}: {exc}")
        return fn

    return decorate


def raises(exc_type, fn, contains=None):
    try:
        fn()
    except exc_type as exc:
        if contains is not None and contains.lower() not in str(exc).lower():
            raise AssertionError(f"message should mention {contains!r}, got: {exc}") from None
        return
    except Exception as exc:  # noqa: BLE001
        raise AssertionError(f"expected {exc_type.__name__}, got {type(exc).__name__}: {exc}") from None
    raise AssertionError(f"expected {exc_type.__name__}, nothing was raised")


print("\nThe ErrorCode contract")


@test("an int ErrorCode 0 is success")
def _():
    f = FakeRequester().queue_json({"ErrorCode": 0, "Result": "ok"})
    assert client(f).send_sms("0501234567", "hello", "MyShop")["ErrorCode"] == 0


@test('a str ErrorCode "0" is also success')
def _():
    f = FakeRequester().queue_json({"ErrorCode": "0", "Result": "ok"})
    assert client(f).send_sms("0501234567", "hello", "MyShop")["ErrorCode"] == "0"


@test("HTTP 200 with ErrorCode 4 still raises")
def _():
    f = FakeRequester().queue_json({"ErrorCode": 4, "Result": "sender not verified"})
    raises(ApiError, lambda: client(f).send_sms("0501234567", "hi", "MyShop"))


@test("ErrorCode 3 is transient, 4 is not")
def _():
    assert ApiError.from_code("3", "x", "CreateContact").is_transient is True
    assert ApiError.from_code("4", "x", "CreateContact").is_transient is False


print("\nAuthentication failure must not be retryable")


@test("ErrorCode 1 raises AuthenticationError and says not to retry")
def _():
    f = FakeRequester().queue_json({"ErrorCode": 1, "Result": "bad key"})
    raises(AuthenticationError, lambda: client(f).send_sms("050", "hi", "MyShop"), "do not retry")


@test("the client latches shut: a caller that loops never reaches the network twice")
def _():
    f = FakeRequester().queue_json({"ErrorCode": 1, "Result": "bad key"})
    c = client(f)
    for _i in range(25):
        try:
            c.send_sms("0501234567", "hi", "MyShop")
        except AuthenticationError:
            pass
    assert len(f.calls) == 1, f"expected 1 request to leave the process, got {len(f.calls)}"


@test("verify_key answers False on a rejected key instead of raising")
def _():
    f = FakeRequester().queue_json({"ErrorCode": 1, "Result": "bad key"})
    assert client(f).verify_key() is False


@test("verify_key answers True on a valid key and writes nothing")
def _():
    f = FakeRequester().queue_json({"ErrorCode": 0, "StatusID": 0})
    c = client(f)
    assert c.verify_key() is True
    assert f.last["method"] == "GET"
    assert f.last["params"]["f"] == "GetContactStatus"


print("\nRequest shape")


@test("the key and a User-Agent are always sent")
def _():
    f = FakeRequester().queue_json({"ErrorCode": 0})
    client(f).send_sms("0501234567", "hi", "MyShop")
    assert f.last["headers"]["ApiKey"] == "test-key"
    assert f.last["headers"]["User-Agent"].startswith("meser10-python/")


@test("the key never travels in the query string")
def _():
    f = FakeRequester().queue_json({"ErrorCode": 0})
    client(f).send_sms("0501234567", "hi", "MyShop")
    assert "test-key" not in json.dumps(f.last["params"])


@test("the function goes in params, the payload in the body")
def _():
    f = FakeRequester().queue_json({"ErrorCode": 0})
    client(f).send_sms("0501234567", "hello", "MyShop")
    assert f.last["params"]["f"] == "SendSingleSmsMessage"
    assert f.last_body() == {
        "ToPhone": "0501234567",
        "MessageBody": "hello",
        "FromName": "MyShop",
    }


@test("GetContactStatus sends the address in params with no body")
def _():
    f = FakeRequester().queue_json({"ErrorCode": 0, "StatusID": 10, "Status": "פעיל"})
    client(f).status("person@example.com")
    assert f.last["method"] == "GET"
    assert f.last["data"] is None
    assert f.last["params"]["email"] == "person@example.com"


@test("Hebrew is sent unescaped")
def _():
    f = FakeRequester().queue_json({"ErrorCode": 0})
    client(f).send_sms("0501234567", "קוד האימות שלך", "MyShop")
    assert "קוד".encode("utf-8") in f.last["data"]


print("\nEmail: the four things the gateway will not do")


@test("reply_to is required and costs no request")
def _():
    f = FakeRequester()
    raises(
        InvalidRequestError,
        lambda: client(f).send_email("a@b.com", "S", "<p>x</p>", "Shop", ""),
        "required",
    )
    assert len(f.calls) == 0


@test("a malformed reply_to is caught before the request")
def _():
    f = FakeRequester()
    raises(InvalidRequestError, lambda: client(f).send_email("a@b.com", "S", "<p>x</p>", "Shop", "nope"))
    assert len(f.calls) == 0


@test("a comma separated recipient list is refused")
def _():
    f = FakeRequester()
    raises(
        InvalidRequestError,
        lambda: client(f).send_email("a@b.com,c@d.com", "S", "<p>x</p>", "Shop", "r@e.com"),
        "one recipient per call",
    )


@test("a valid email sends exactly the five accepted fields")
def _():
    f = FakeRequester().queue_json({"ErrorCode": 0})
    client(f).send_email("a@b.com", "Receipt", "<p>Thanks</p>", "MyShop", "orders@shop.com")
    assert list(f.last_body().keys()) == [
        "ToEMail",
        "Subject",
        "Body",
        "FromName",
        "ReplyToEMail",
    ]


print("\nSender name rules")

for ok in ("MyShop", "MESER10", "Meser10 Ltd", "A1", "0501234567", "+972501234567"):

    @test(f"accepts {ok!r}")
    def _(value=ok):
        assert_sender_is_well_formed(value)


@test("rejects a name longer than 11 characters")
def _():
    raises(InvalidRequestError, lambda: assert_sender_is_well_formed("Meser10 Israel"), "14 characters")


@test("rejects Hebrew as a sender name")
def _():
    raises(InvalidRequestError, lambda: assert_sender_is_well_formed("מסר 10"), "Hebrew")


@test("rejects a digits-only short value")
def _():
    raises(InvalidRequestError, lambda: assert_sender_is_well_formed("12345"), "no letter")


@test("a bad sender name costs no request")
def _():
    f = FakeRequester()
    raises(InvalidRequestError, lambda: client(f).send_sms("0501234567", "hi", "מסר 10"))
    assert len(f.calls) == 0


print("\nSMS part counting")


@test("160 Latin characters are one part, 161 are two")
def _():
    assert sms_parts("a" * 160) == 1
    assert sms_parts("a" * 161) == 2


@test("70 Hebrew characters are one part, 71 are two")
def _():
    assert sms_parts("א" * 70) == 1
    assert sms_parts("א" * 71) == 2


@test("one Hebrew letter makes the whole message Unicode")
def _():
    assert sms_parts("a" * 100 + "א") == 2


print("\nContact status: two ids mean active")


@test("StatusID 10 is mailable")
def _():
    f = FakeRequester().queue_json({"ErrorCode": 0, "StatusID": 10, "Status": "פעיל"})
    s = client(f).status("a@b.com")
    assert s.is_mailable and s.exists and s.name == "active"


@test("StatusID 30 is mailable too, which is the one people get wrong")
def _():
    f = FakeRequester().queue_json({"ErrorCode": 0, "StatusID": 30})
    s = client(f).status("a@b.com")
    assert s.is_mailable and s.name == "active_reactivated"


@test("StatusID 0 means not on the account")
def _():
    f = FakeRequester().queue_json({"ErrorCode": 0, "StatusID": 0})
    s = client(f).status("a@b.com")
    assert s.exists is False and s.is_mailable is False


@test("40 is bounced and 50 is unsubscribed, neither mailable")
def _():
    for status_id, attr in ((40, "is_bounced"), (50, "is_unsubscribed")):
        f = FakeRequester().queue_json({"ErrorCode": 0, "StatusID": status_id})
        s = client(f).status("a@b.com")
        assert getattr(s, attr) is True
        assert s.is_mailable is False


@test("a string StatusID is coerced")
def _():
    f = FakeRequester().queue_json({"ErrorCode": 0, "StatusID": "30"})
    assert client(f).status("a@b.com").is_mailable is True


@test("an unknown id is named rather than guessed")
def _():
    assert ContactStatus(99).name == "unknown_99"
    assert ContactStatus(99).is_mailable is False


print("\nContacts")


@test("unknown contact fields are refused, with the right spelling offered")
def _():
    f = FakeRequester()
    raises(
        InvalidRequestError,
        lambda: client(f).create_contact("Newsletter", email="a@b.com"),
        "capitalisation",
    )
    assert len(f.calls) == 0


@test("a contact needs an EMail or a PhoneNo")
def _():
    f = FakeRequester()
    raises(InvalidRequestError, lambda: client(f).create_contact("Newsletter", FirstName="Dana"))


@test("empty fields are omitted rather than sent as empty strings")
def _():
    f = FakeRequester().queue_json({"ErrorCode": 0})
    client(f).create_contact("Newsletter", EMail="a@b.com", LastName="  ")
    assert f.last_body() == {"ContactListName": "Newsletter", "EMail": "a@b.com"}


@test("change_contact_status picks EMail or PhoneNo by what it was given")
def _():
    f = FakeRequester().queue_json({"ErrorCode": 0}).queue_json({"ErrorCode": 0})
    c = client(f)
    c.change_contact_status("a@b.com", "Unsubscribed")
    assert "EMail" in f.last_body()
    c.change_contact_status("0501234567", "Bounced")
    assert "PhoneNo" in f.last_body()


@test("an unknown status value is refused locally")
def _():
    f = FakeRequester()
    raises(InvalidRequestError, lambda: client(f).change_contact_status("a@b.com", "Removed"))
    assert len(f.calls) == 0


print("\nWhen the answer is not JSON")


@test("a Cloudflare HTML page is reported as what it is")
def _():
    f = FakeRequester().queue_raw(403, "<html>Cloudflare Browser Integrity Check</html>")
    raises(TransportError, lambda: client(f).send_sms("0501234567", "hi", "MyShop"), "Cloudflare")


@test("any other non JSON body is reported with its status")
def _():
    f = FakeRequester().queue_raw(502, "Bad Gateway")
    raises(TransportError, lambda: client(f).send_sms("0501234567", "hi", "MyShop"), "502")


print("\nConstruction")


@test("an empty key is refused at construction")
def _():
    raises(InvalidRequestError, lambda: Meser10Client("   ", requester=FakeRequester()))


@test("the User-Agent can be overridden")
def _():
    f = FakeRequester().queue_json({"ErrorCode": 0})
    Meser10Client("k", user_agent="acme-crm/2.1", requester=f).send_sms("0501234567", "hi", "MyShop")
    assert f.last["headers"]["User-Agent"] == "acme-crm/2.1"


print()
if FAILED:
    print(f"{len(FAILED)} failed, {PASSED} passed\n")
    sys.exit(1)

print(f"{PASSED} passed, 0 failed\n")
