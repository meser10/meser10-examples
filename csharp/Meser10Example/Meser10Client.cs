using System.Net;
using System.Text;
using System.Text.Json;

namespace Meser10;

/// <summary>
/// Client for the Meser 10 JSON gateway.
///
/// Five functions: one SMS, one email, add or update a contact, change a
/// contact's status, read a contact's status. Campaigns, lists, groups,
/// reporting and attachments are on the SOAP service, not here.
///
/// Three behaviours of the gateway are handled for you, because each one has
/// cost somebody a day:
///
/// <list type="bullet">
/// <item>Every call answers HTTP 200, including a rejected key. Success lives in
/// ErrorCode, which is a number on some functions and a string on others. This
/// client normalises it and throws on anything but zero.</item>
/// <item>Repeated authentication failures block the calling IP address for
/// several hours, and the block is on the address, not the key. So this client
/// has no retry logic at all, and after one authentication failure it latches
/// shut and refuses to call again, even if your code loops.</item>
/// <item>The hosts sit behind Cloudflare with Browser Integrity Check on, which
/// reads the User-Agent header and blocks default library signatures. A
/// User-Agent is always sent.</item>
/// </list>
///
/// Hold one instance for the lifetime of your application, the way you would any
/// HttpClient. Creating one per call exhausts sockets.
/// </summary>
public sealed class Meser10Client : IDisposable
{
    public const string Version = "1.0.0";

    private const string DefaultBaseUrl = "https://heb.mesereser.com/Services/JsonServices.aspx";

    /// <summary>An address no account has, for probing a key without writing anything.</summary>
    private const string ProbeAddress = "nobody.probe@example.invalid";

    private static readonly string[] AllowedContactFields =
    [
        "EMail", "PhoneNo", "FirstName", "LastName", "Address", "City", "Zipcode",
        "CustomField1", "CustomField2", "CustomField3", "CustomField4", "CustomField5",
    ];

    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// Set once an authentication failure has been seen, and never cleared.
    /// Build a new client with a new key rather than retrying with this one.
    /// </summary>
    private AuthenticationException? _locked;

    /// <param name="apiKey">
    /// Issued per account in the Meser 10 interface, under account settings,
    /// advanced settings, API settings.
    /// </param>
    /// <param name="userAgent">
    /// Cloudflare reads this and blocks default library signatures. Any custom
    /// string passes.
    /// </param>
    /// <param name="httpClient">
    /// Pass your own, from IHttpClientFactory, to reuse connections and to test
    /// without a network. When you do, this client will not dispose it.
    /// </param>
    public Meser10Client(
        string apiKey,
        string? userAgent = null,
        string? baseUrl = null,
        TimeSpan? timeout = null,
        HttpClient? httpClient = null)
    {
        string key = (apiKey ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            throw new InvalidRequestException("The API key is empty.");
        }

        _apiKey = key;
        _baseUrl = baseUrl ?? DefaultBaseUrl;
        _ownsHttpClient = httpClient is null;

        _http = httpClient ?? new HttpClient(new HttpClientHandler
        {
            // No automatic retries anywhere in this client. See _locked.
            AllowAutoRedirect = false,
        });

        _http.Timeout = timeout ?? TimeSpan.FromSeconds(20);

        if (!_http.DefaultRequestHeaders.Contains("ApiKey"))
        {
            _http.DefaultRequestHeaders.Add("ApiKey", _apiKey);
        }

        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.Add("User-Agent", userAgent ?? $"meser10-csharp/{Version}");
        }
    }

    /* ------------------------------------------------------------- messaging */

    /// <summary>
    /// Sends one SMS to one recipient, immediately.
    ///
    /// MessageID comes back as 0, and the gateway has no way to read a message's
    /// delivery state, so keep your own identifier if you need to trace a send.
    /// </summary>
    /// <param name="to">One recipient. Israeli local form or E.164.</param>
    /// <param name="body">
    /// Hebrew is sent as Unicode, which shortens a single part from 160
    /// characters to 70. <see cref="Sms.Parts"/> counts.
    /// </param>
    /// <param name="from">
    /// A sender identity already approved on the account. Validated here before
    /// the request is spent.
    /// </param>
    public Task<JsonElement> SendSmsAsync(
        string to,
        string body,
        string from,
        CancellationToken cancellationToken = default)
    {
        string phone = (to ?? string.Empty).Trim();
        string text = (body ?? string.Empty).Trim();

        if (phone.Length == 0)
        {
            throw new InvalidRequestException("No recipient. One SMS goes to one number.");
        }

        if (text.Length == 0)
        {
            throw new InvalidRequestException("The message body is empty.");
        }

        Sms.AssertSenderIsWellFormed(from);

        return PostAsync("SendSingleSmsMessage", new Dictionary<string, string>
        {
            ["ToPhone"] = phone,
            ["MessageBody"] = text,
            ["FromName"] = from.Trim(),
        }, cancellationToken);
    }

    /// <summary>
    /// Sends one email to one recipient, immediately.
    ///
    /// The gateway accepts no From address (only a display name), no CC, no BCC,
    /// no attachments and no second recipient. There is deliberately no way to
    /// pass any of those: if a message needs them, this is not its transport.
    /// </summary>
    /// <param name="html">HTML. Set dir="rtl" yourself on Hebrew content.</param>
    /// <param name="from">A display name only. The address is the account's.</param>
    /// <param name="replyTo">
    /// Required by the gateway, whatever older documentation shows. Omitting it
    /// answers a null reference error with ErrorCode 3.
    /// </param>
    public Task<JsonElement> SendEmailAsync(
        string to,
        string subject,
        string html,
        string from,
        string replyTo,
        CancellationToken cancellationToken = default)
    {
        foreach ((string what, string? value) in new[]
                 {
                     ("recipient", to), ("subject", subject), ("body", html), ("sender name", from),
                 })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidRequestException($"The {what} is empty.");
            }
        }

        string address = (replyTo ?? string.Empty).Trim();
        if (address.Length == 0)
        {
            throw new InvalidRequestException(
                "replyTo is required by the gateway. Without it the call fails with an application "
                + "error.");
        }

        if (!IsEmail(address))
        {
            throw new InvalidRequestException($"replyTo is not a valid address: {address}");
        }

        if (to.Contains(',') || to.Contains(';'))
        {
            throw new InvalidRequestException(
                "One recipient per call. Send a separate message per address rather than a list.");
        }

        return PostAsync("SendSingleEMailMessage", new Dictionary<string, string>
        {
            ["ToEMail"] = to.Trim(),
            ["Subject"] = subject,
            ["Body"] = html,
            ["FromName"] = from.Trim(),
            ["ReplyToEMail"] = address,
        }, cancellationToken);
    }

    /* -------------------------------------------------------------- contacts */

    /// <summary>
    /// Adds a contact to a named list, or updates them if they already exist.
    ///
    /// The list is not created for you. A name that does not exist on the same
    /// account as the key fails, and the failure names the list.
    ///
    /// Unknown keys are refused rather than silently dropped, because "email"
    /// instead of "EMail" is the easiest mistake to make against this gateway
    /// and it fails looking like nothing happened.
    /// </summary>
    public Task<JsonElement> CreateContactAsync(
        string listName,
        IDictionary<string, string> fields,
        CancellationToken cancellationToken = default)
    {
        string list = (listName ?? string.Empty).Trim();
        if (list.Length == 0)
        {
            throw new InvalidRequestException("No list name. The gateway will not guess one.");
        }

        var payload = new Dictionary<string, string> { ["ContactListName"] = list };

        foreach ((string key, string value) in fields ?? new Dictionary<string, string>())
        {
            if (!AllowedContactFields.Contains(key))
            {
                throw new InvalidRequestException(
                    $"Unknown contact field '{key}'. The gateway accepts: "
                    + string.Join(", ", AllowedContactFields)
                    + ". Note the capitalisation of EMail and PhoneNo.");
            }

            string trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length > 0)
            {
                payload[key] = trimmed;
            }
        }

        if (!payload.ContainsKey("EMail") && !payload.ContainsKey("PhoneNo"))
        {
            throw new InvalidRequestException("A contact needs at least an EMail or a PhoneNo.");
        }

        return PostAsync("CreateContact", payload, cancellationToken);
    }

    /// <summary>
    /// Moves a contact to Active, Unsubscribed or Bounced.
    ///
    /// An address which is not on the account also answers success, so this
    /// cannot tell you whether the contact existed. Use
    /// <see cref="StatusAsync"/> for that.
    /// </summary>
    public Task<JsonElement> ChangeContactStatusAsync(
        string emailOrPhone,
        string status,
        CancellationToken cancellationToken = default)
    {
        string who = (emailOrPhone ?? string.Empty).Trim();
        if (who.Length == 0)
        {
            throw new InvalidRequestException("No contact given.");
        }

        if (status is not (ContactStatusValue.Active
                        or ContactStatusValue.Unsubscribed
                        or ContactStatusValue.Bounced))
        {
            throw new InvalidRequestException(
                "Status must be one of Active, Unsubscribed, Bounced, got "
                + $"'{status}'.");
        }

        string key = IsEmail(who) ? "EMail" : "PhoneNo";

        return PostAsync("ChangeContactStatus", new Dictionary<string, string>
        {
            [key] = who,
            ["Status"] = status,
        }, cancellationToken);
    }

    /// <summary>
    /// Reads a contact's current status.
    ///
    /// Only an email address works here: the gateway does not read a phone
    /// parameter, and the address has to travel on the query string, which this
    /// method does for you.
    /// </summary>
    public async Task<ContactStatus> StatusAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        string address = (email ?? string.Empty).Trim();
        if (address.Length == 0)
        {
            throw new InvalidRequestException("No address given.");
        }

        JsonElement body = await GetAsync(
            "GetContactStatus",
            new Dictionary<string, string> { ["email"] = address },
            cancellationToken).ConfigureAwait(false);

        return ContactStatus.FromResponse(body);
    }

    /// <summary>
    /// Is this key accepted? Creates nothing, sends nothing, spends nothing.
    ///
    /// Use this instead of retrying a real call when you suspect a key problem.
    /// Retrying is what gets an IP address blocked.
    /// </summary>
    public async Task<bool> VerifyKeyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await GetAsync(
                "GetContactStatus",
                new Dictionary<string, string> { ["email"] = ProbeAddress },
                cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (AuthenticationException)
        {
            return false;
        }
    }

    /* -------------------------------------------------------------- plumbing */

    private Task<JsonElement> PostAsync(
        string function,
        Dictionary<string, string> payload,
        CancellationToken cancellationToken)
    {
        // JavaScriptEncoder is left at its default here; the gateway reads the
        // escaped form correctly, so Hebrew arriving as ק is fine on the wire.
        string json = JsonSerializer.Serialize(payload);

        return CallAsync(HttpMethod.Post, function, null, json, cancellationToken);
    }

    private Task<JsonElement> GetAsync(
        string function,
        Dictionary<string, string> query,
        CancellationToken cancellationToken)
    {
        return CallAsync(HttpMethod.Get, function, query, null, cancellationToken);
    }

    private async Task<JsonElement> CallAsync(
        HttpMethod method,
        string function,
        Dictionary<string, string>? query,
        string? body,
        CancellationToken cancellationToken)
    {
        if (_locked is not null)
        {
            // Deliberate: a caller that loops on failure must not reach the
            // network again, because that is what blocks the IP address.
            throw _locked;
        }

        var parameters = new List<string> { $"f={Uri.EscapeDataString(function)}" };
        if (query is not null)
        {
            foreach ((string key, string value) in query)
            {
                parameters.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
            }
        }

        using var request = new HttpRequestMessage(method, $"{_baseUrl}?{string.Join('&', parameters)}");

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransportException(
                $"The request did not complete within {_http.Timeout.TotalSeconds:0} seconds.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new TransportException($"The request did not complete: {exception.Message}", exception);
        }

        using (response)
        {
            string raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(raw);
            }
            catch (JsonException exception)
            {
                if (response.StatusCode == HttpStatusCode.Forbidden
                    || raw.Contains("cloudflare", StringComparison.OrdinalIgnoreCase))
                {
                    throw new TransportException(
                        "Cloudflare refused the request, which happens when the User-Agent is a "
                        + "default library signature. This client sets one, so check whether a proxy "
                        + $"is replacing it. HTTP status was {(int)response.StatusCode}.",
                        exception);
                }

                string snippet = raw.Trim();
                throw new TransportException(
                    "The gateway answered something that is not JSON (HTTP "
                    + $"{(int)response.StatusCode}): "
                    + snippet[..Math.Min(200, snippet.Length)],
                    exception);
            }

            using (document)
            {
                JsonElement root = document.RootElement.Clone();

                // ErrorCode is a number on some functions and a string on others.
                string code = ReadAsString(root, "ErrorCode");
                string result = ReadAsString(root, "Result");

                if (code == "0")
                {
                    return root;
                }

                if (code == "1")
                {
                    _locked = new AuthenticationException(
                        "The API key was rejected. Do not retry: repeated authentication failures "
                        + "block the calling IP address for several hours, and the block is on the "
                        + "address, not the key, so reissuing the key and trying again makes it "
                        + $"worse. Gateway said: {result}");

                    throw _locked;
                }

                throw ApiException.FromCode(code, result, function);
            }
        }
    }

    private static string ReadAsString(JsonElement body, string property)
    {
        if (!body.TryGetProperty(property, out JsonElement value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.ToString(),
            _ => string.Empty,
        };
    }

    private static bool IsEmail(string value)
    {
        int at = value.IndexOf('@');

        return at > 0
            && at == value.LastIndexOf('@')
            && at < value.Length - 1
            && value.IndexOf('.', at) > at + 1
            && !value.Any(char.IsWhiteSpace);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
