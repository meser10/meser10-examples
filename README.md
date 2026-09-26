# Meser 10 API examples

Working code for sending transactional SMS and email through the [Meser 10](https://www.meser10.co.il) JSON API, in four languages and in plain curl. One-time passwords, receipts, contact sync.

Every example here runs. Each language's tests or self-checks use a fake transport, so you can run them with no key and nothing is sent.

| | What is here | Run the checks |
|---|---|---|
| **PHP** | [`meser10/api-client`](https://packagist.org/packages/meser10/api-client), a Composer package | `composer require meser10/api-client` |
| **Node** | [`@meser10/api-client`](https://www.npmjs.com/package/@meser10/api-client), typed, zero dependencies | `npm install @meser10/api-client` |
| **Python** | [`python/`](python/) a single-file client plus three examples | `cd python && python3 test_client.py` |
| **C#** | [`csharp/`](csharp/) a client and an OTP example on net8.0 | `cd csharp/Meser10Example && dotnet run -- selftest` |
| **curl** | [`curl/`](curl/) the smallest possible reference | `MESER10_API_KEY=... ./curl/verify-key.sh` |

Python and C# have no published package yet, so those directories hold a client you copy into your project. The PHP and Node clients are installable and live in their own repositories.

## Start here, whatever the language

```
MESER10_API_KEY=... ./curl/verify-key.sh
```

It probes an address that is on no account, so a valid key answers `ErrorCode` 0 with `StatusID` 0, a rejected key answers `ErrorCode` 1, and nothing is created either way. Run it before anything else, and run it whenever you suspect a key problem.

## The three behaviours every example handles

Each one has cost somebody a working day. If you write your own client instead of using these, handle all three.

### 1. HTTP 200 comes back for every outcome, including a rejected key

The status code proves nothing. `ErrorCode` is the only reliable indicator, and it is serialised as a number on some functions and as a string on others, so normalise before comparing:

```
0  success
1  the key is wrong, missing or revoked    -> stop, do not retry
3  application error, or an unprocessable payload -> one retry is reasonable
4  a parameter is missing or invalid       -> usually a list that does not exist,
                                              or a sender name not yet approved
6  unknown function name                   -> fix the f= value
```

`Result` carries a human-readable message, in Hebrew on most failures. Every message quoted in this repository is an English translation, not the string on the wire, so do not match on it.

### 2. An authentication failure must never be retried

Repeated failures block the **calling IP address** for several hours. The block is on the address rather than the key, so reissuing the key and trying again makes it worse, and on shared hosting it takes down every other integration sending from that machine.

Every client here has no retry logic at all, and after one authentication failure it latches shut and refuses to reach the network again, even if your code loops. Each language has a test that runs 25 attempts and asserts that exactly one request left the process.

### 3. Set a User-Agent header

The API hosts sit behind Cloudflare with Browser Integrity Check on, and it reads that header. Default library signatures are blocked:

| Sends | Result |
|---|---|
| `Java/1.8.0_241` | 403 |
| `Python-urllib/3.x` | Cloudflare error 1010 |
| Any custom string, a browser string, curl, python-requests, no header at all | passes |

The value itself does not matter. The symptom is intermittent, because a system often has two code paths to the same endpoint and only one of them sets the header. This is the single most common cause of "it works on my machine".

## Two more things that catch people

**Two status ids mean active.** A contact created through the API returns `StatusID` 10; one moved back to Active after a bounce or an unsubscribe returns 30. Code that checks for 10 alone silently drops every reactivated contact. Every client here exposes `isMailable` rather than making you remember that.

**The sender name rules are stricter than they look.** An alphanumeric SMS sender name is at most 11 characters, Latin letters, digits and spaces only, and must contain at least one letter, so Hebrew text and digits-only values are rejected. A too-long name is not truncated, the call is simply rejected. All the clients check locally before spending a request:

```
MyShop           fine
Meser10 Ltd      fine, exactly 11
Meser10 Israel   rejected, 14 characters
מסר 10           rejected, Hebrew cannot be a sender name
0501234567       fine, a number
```

The 11-character limit is a GSM constraint on alphanumeric sender IDs, not a Meser 10 limit. Support varies by destination country: alphanumeric sender IDs are not available in the United States or Canada, where a number is used instead.

## Hebrew

One Hebrew letter anywhere pushes an entire SMS to Unicode, which takes a single part from 160 characters down to 70 and changes what you are billed. Every client has a part counter.

For email, set `dir="rtl"` in your own HTML. Nothing does it for you, and Hebrew mail sent without it gets left aligned by some clients. That is the usual cause of "the email looks broken".

## What this gateway cannot do

Stated plainly so nobody discovers it mid-build:

- **No webhooks.** Anything that has to react to an event polls for it.
- **One recipient per call**, for both SMS and email.
- **Email takes no From address** (a display name only), no CC, no BCC and no attachments.
- **No list-reading function**, so a JSON-only integration cannot offer a dropdown of the account's lists and has to ask for the list name.
- **No sandbox.** The key probe above is the closest thing.

Campaigns, mailing lists, groups, reporting, attachments and user management are on the SOAP service at `https://ns.mesereser.com/Services/Services.asmx?wsdl`, which exposes 61 operations and uses the same key.

## The machine-readable contract

- [OpenAPI 3.1 description](https://www.meser10.co.il/api-docs/meser10-json-api.json)
- [Postman collection](https://www.meser10.co.il/api-docs/meser10-json-api.postman_collection.json)
- [English reference](https://www.meser10.co.il/en/json-api/)
- [Hebrew documentation](https://www.meser10.co.il/api-docs/)

## Getting a key

Open an account, then find the key in the interface under account settings, advanced settings, API settings.

Never put a key in a support message, a bug report, an issue or a commit. If one has been shared anywhere, reissue it.

## Support

support@meser10.co.il, Sunday to Thursday.

## Licence

MIT.
