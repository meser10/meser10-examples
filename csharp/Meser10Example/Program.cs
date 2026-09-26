using System.Net;
using System.Security.Cryptography;
using System.Text;

using Meser10;

namespace Meser10Example;

/// <summary>
/// Two entry points in one file.
///
///   dotnet run -- selftest              runs the checks below against a fake
///                                       handler. No network, no credentials,
///                                       nothing sent.
///   dotnet run -- otp 0501234567        sends a real one-time password.
///                                       Needs MESER10_API_KEY and spends credit.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string command = args.Length > 0 ? args[0] : "selftest";

        return command switch
        {
            "otp" => await SendOtpAsync(args.Length > 1 ? args[1] : string.Empty).ConfigureAwait(false),
            "selftest" => SelfTest.Run(),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: dotnet run -- selftest | dotnet run -- otp <phone>");

        return 1;
    }

    /// <summary>
    /// Sending a one-time password over SMS.
    ///
    /// Note what this does NOT do: it does not store the code or check it. That
    /// belongs in your application, next to your session and your rate limiting,
    /// not in a messaging client. What matters here is the sending.
    /// </summary>
    private static async Task<int> SendOtpAsync(string phone)
    {
        string? key = Environment.GetEnvironmentVariable("MESER10_API_KEY");

        if (string.IsNullOrWhiteSpace(key))
        {
            Console.Error.WriteLine("Set MESER10_API_KEY first.");

            return 1;
        }

        if (string.IsNullOrWhiteSpace(phone))
        {
            Console.Error.WriteLine("Usage: dotnet run -- otp <phone>");

            return 1;
        }

        // Your application owns the code and its lifetime. This is an example value.
        string code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

        // One client for the lifetime of the application. Creating one per call
        // exhausts sockets, which is the classic HttpClient mistake.
        using var client = new Meser10Client(key, userAgent: "my-app/1.0");

        string text = $"קוד האימות שלך הוא {code}. הקוד תקף ל-5 דקות.";

        // Hebrew goes out as Unicode, so a single part holds 70 characters, not 160.
        Console.WriteLine($"This message will be billed as {Sms.Parts(text)} part(s).");

        try
        {
            await client.SendSmsAsync(phone, text, "MyShop").ConfigureAwait(false);
            Console.WriteLine("Accepted for delivery.");

            return 0;
        }
        catch (InvalidRequestException exception)
        {
            // Refused before the network. Nothing sent, nothing spent.
            Console.Error.WriteLine($"Bad call: {exception.Message}");

            return 2;
        }
        catch (AuthenticationException exception)
        {
            // Stop. Never loop here: repeated failures block this IP address for
            // hours, and the block is on the address, not the key.
            Console.Error.WriteLine($"Key rejected. Stopping: {exception.Message}");

            return 3;
        }
        catch (ApiException exception)
        {
            // ErrorCode 4 on an SMS almost always means the sender identity is
            // not approved on the account yet.
            Console.Error.WriteLine(
                $"Gateway refused it (ErrorCode {exception.ErrorCode}): {exception.Message}");

            return 4;
        }
        catch (TransportException exception)
        {
            Console.Error.WriteLine($"Never got an answer: {exception.Message}");

            return 5;
        }

        // One honest caveat before building a login on this: "accepted for
        // delivery" is as much as the gateway will tell you. There is no
        // per-message delivery status and no webhook, so you cannot confirm the
        // code reached the handset. Offer a resend after a short wait, and a
        // second route in, rather than assuming delivery.
    }
}

/// <summary>
/// A queue of canned answers, so the checks below exercise the real client
/// without a network. This is also how you test your own code against it.
/// </summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _queue = new();

    public List<HttpRequestMessage> Calls { get; } = [];

    public List<string> Bodies { get; } = [];

    public FakeHandler Queue(HttpStatusCode status, string body)
    {
        _queue.Enqueue((status, body));

        return this;
    }

    public FakeHandler QueueJson(string json) => Queue(HttpStatusCode.OK, json);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Calls.Add(request);
        Bodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

        if (_queue.Count == 0)
        {
            throw new InvalidOperationException("The fake handler was called more times than queued.");
        }

        (HttpStatusCode status, string body) = _queue.Dequeue();

        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}

internal static class SelfTest
{
    private static int _passed;
    private static readonly List<string> Failed = [];

    public static int Run()
    {
        Check("a numeric ErrorCode 0 is success", () =>
        {
            var handler = new FakeHandler().QueueJson("""{"ErrorCode":0,"Result":"ok"}""");
            using var client = Client(handler);
            client.SendSmsAsync("0501234567", "hello", "MyShop").GetAwaiter().GetResult();
            Expect(1, handler.Calls.Count, "one request");
        });

        Check("a string ErrorCode \"0\" is also success", () =>
        {
            var handler = new FakeHandler().QueueJson("""{"ErrorCode":"0","Result":"ok"}""");
            using var client = Client(handler);
            client.SendSmsAsync("0501234567", "hello", "MyShop").GetAwaiter().GetResult();
        });

        Check("HTTP 200 with ErrorCode 4 still throws", () =>
        {
            var handler = new FakeHandler().QueueJson("""{"ErrorCode":4,"Result":"not verified"}""");
            using var client = Client(handler);
            Throws<ApiException>(() => client.SendSmsAsync("0501234567", "hi", "MyShop").GetAwaiter().GetResult());
        });

        Check("ErrorCode 3 is transient, 4 is not", () =>
        {
            Expect(true, ApiException.FromCode("3", "x", "f").IsTransient, "3 transient");
            Expect(false, ApiException.FromCode("4", "x", "f").IsTransient, "4 transient");
        });

        Check("ErrorCode 1 throws AuthenticationException and says not to retry", () =>
        {
            var handler = new FakeHandler().QueueJson("""{"ErrorCode":1,"Result":"bad key"}""");
            using var client = Client(handler);
            var error = Throws<AuthenticationException>(
                () => client.SendSmsAsync("0501234567", "hi", "MyShop").GetAwaiter().GetResult());
            if (!error.Message.Contains("Do not retry", StringComparison.Ordinal))
            {
                throw new Exception("the message should tell the caller not to retry");
            }
        });

        Check("the client latches shut: a caller that loops never reaches the network twice", () =>
        {
            var handler = new FakeHandler().QueueJson("""{"ErrorCode":1,"Result":"bad key"}""");
            using var client = Client(handler);

            for (int i = 0; i < 25; i++)
            {
                try
                {
                    client.SendSmsAsync("0501234567", "hi", "MyShop").GetAwaiter().GetResult();
                }
                catch (AuthenticationException)
                {
                    // what a naive caller does
                }
            }

            Expect(1, handler.Calls.Count, "exactly one request should have left the process");
        });

        Check("verifyKey answers false on a rejected key instead of throwing", () =>
        {
            var handler = new FakeHandler().QueueJson("""{"ErrorCode":1,"Result":"bad key"}""");
            using var client = Client(handler);
            Expect(false, client.VerifyKeyAsync().GetAwaiter().GetResult(), "verifyKey");
        });

        Check("the key and a User-Agent are always sent, and never in the URL", () =>
        {
            var handler = new FakeHandler().QueueJson("""{"ErrorCode":0}""");
            using var client = Client(handler);
            client.SendSmsAsync("0501234567", "hi", "MyShop").GetAwaiter().GetResult();

            HttpRequestMessage call = handler.Calls[0];
            Expect(true, call.Headers.Contains("ApiKey"), "ApiKey header");
            Expect(true, call.Headers.UserAgent.Count > 0, "User-Agent header");
            Expect(false, call.RequestUri!.ToString().Contains("test-key", StringComparison.Ordinal), "key in URL");
        });

        Check("the function name goes on the query string", () =>
        {
            var handler = new FakeHandler().QueueJson("""{"ErrorCode":0}""");
            using var client = Client(handler);
            client.SendSmsAsync("0501234567", "hi", "MyShop").GetAwaiter().GetResult();
            Expect(true,
                handler.Calls[0].RequestUri!.Query.Contains("f=SendSingleSmsMessage", StringComparison.Ordinal),
                "f on the query string");
        });

        Check("GetContactStatus is a GET with the address on the query string and no body", () =>
        {
            var handler = new FakeHandler().QueueJson("""{"ErrorCode":0,"StatusID":10,"Status":"פעיל"}""");
            using var client = Client(handler);
            client.StatusAsync("person@example.com").GetAwaiter().GetResult();

            Expect(HttpMethod.Get, handler.Calls[0].Method, "method");
            Expect(true, handler.Calls[0].RequestUri!.Query.Contains("email=person", StringComparison.Ordinal), "email param");
            Expect(string.Empty, handler.Bodies[0], "body");
        });

        Check("replyTo is required and costs no request", () =>
        {
            var handler = new FakeHandler();
            using var client = Client(handler);
            Throws<InvalidRequestException>(
                () => client.SendEmailAsync("a@b.com", "S", "<p>x</p>", "Shop", "").GetAwaiter().GetResult());
            Expect(0, handler.Calls.Count, "nothing sent");
        });

        Check("a comma separated recipient list is refused", () =>
        {
            var handler = new FakeHandler();
            using var client = Client(handler);
            Throws<InvalidRequestException>(
                () => client.SendEmailAsync("a@b.com,c@d.com", "S", "<p>x</p>", "Shop", "r@e.com")
                    .GetAwaiter().GetResult());
            Expect(0, handler.Calls.Count, "nothing sent");
        });

        Check("sender names: the rules", () =>
        {
            foreach (string ok in new[] { "MyShop", "MESER10", "Meser10 Ltd", "A1", "0501234567", "+972501234567" })
            {
                Sms.AssertSenderIsWellFormed(ok);
            }

            Throws<InvalidRequestException>(() => Sms.AssertSenderIsWellFormed("Meser10 Israel"));
            Throws<InvalidRequestException>(() => Sms.AssertSenderIsWellFormed("מסר 10"));
            Throws<InvalidRequestException>(() => Sms.AssertSenderIsWellFormed("12345"));
            Throws<InvalidRequestException>(() => Sms.AssertSenderIsWellFormed("My-Shop"));
        });

        Check("a bad sender name costs no request", () =>
        {
            var handler = new FakeHandler();
            using var client = Client(handler);
            Throws<InvalidRequestException>(
                () => client.SendSmsAsync("0501234567", "hi", "מסר 10").GetAwaiter().GetResult());
            Expect(0, handler.Calls.Count, "nothing sent");
        });

        Check("SMS parts: 160 Latin is one, 70 Hebrew is one, one Hebrew letter flips it", () =>
        {
            Expect(1, Sms.Parts(new string('a', 160)), "160 Latin");
            Expect(2, Sms.Parts(new string('a', 161)), "161 Latin");
            Expect(1, Sms.Parts(new string('א', 70)), "70 Hebrew");
            Expect(2, Sms.Parts(new string('א', 71)), "71 Hebrew");
            Expect(2, Sms.Parts(new string('a', 100) + "א"), "one Hebrew letter");
            Expect(0, Sms.Parts(string.Empty), "empty");
        });

        Check("StatusID 30 is mailable too, which is the one people get wrong", () =>
        {
            var handler = new FakeHandler().QueueJson("""{"ErrorCode":0,"StatusID":30}""");
            using var client = Client(handler);
            ContactStatus status = client.StatusAsync("a@b.com").GetAwaiter().GetResult();
            Expect(true, status.IsMailable, "30 mailable");
            Expect("active_reactivated", status.Name, "name");
        });

        Check("a string StatusID is coerced", () =>
        {
            var handler = new FakeHandler().QueueJson("""{"ErrorCode":0,"StatusID":"30"}""");
            using var client = Client(handler);
            Expect(true, client.StatusAsync("a@b.com").GetAwaiter().GetResult().IsMailable, "string 30");
        });

        Check("StatusID 0, 40 and 50 are not mailable", () =>
        {
            foreach (int id in new[] { 0, 40, 50 })
            {
                var handler = new FakeHandler().QueueJson($"{{\"ErrorCode\":0,\"StatusID\":{id}}}");
                using var client = Client(handler);
                Expect(false, client.StatusAsync("a@b.com").GetAwaiter().GetResult().IsMailable, $"id {id}");
            }
        });

        Check("unknown contact fields are refused, with the right spelling offered", () =>
        {
            var handler = new FakeHandler();
            using var client = Client(handler);
            var error = Throws<InvalidRequestException>(() =>
                client.CreateContactAsync("Newsletter", new Dictionary<string, string> { ["email"] = "a@b.com" })
                    .GetAwaiter().GetResult());
            Expect(true, error.Message.Contains("capitalisation", StringComparison.Ordinal), "advice");
            Expect(0, handler.Calls.Count, "nothing sent");
        });

        Check("empty contact fields are omitted rather than sent as empty strings", () =>
        {
            var handler = new FakeHandler().QueueJson("""{"ErrorCode":0}""");
            using var client = Client(handler);
            client.CreateContactAsync("Newsletter", new Dictionary<string, string>
            {
                ["EMail"] = "a@b.com",
                ["LastName"] = "  ",
            }).GetAwaiter().GetResult();

            Expect(false, handler.Bodies[0].Contains("LastName", StringComparison.Ordinal), "LastName omitted");
        });

        Check("an unknown status value is refused locally", () =>
        {
            var handler = new FakeHandler();
            using var client = Client(handler);
            Throws<InvalidRequestException>(
                () => client.ChangeContactStatusAsync("a@b.com", "Removed").GetAwaiter().GetResult());
            Expect(0, handler.Calls.Count, "nothing sent");
        });

        Check("a Cloudflare HTML page is reported as what it is", () =>
        {
            var handler = new FakeHandler().Queue(HttpStatusCode.Forbidden, "<html>Cloudflare</html>");
            using var client = Client(handler);
            var error = Throws<TransportException>(
                () => client.SendSmsAsync("0501234567", "hi", "MyShop").GetAwaiter().GetResult());
            Expect(true, error.Message.Contains("Cloudflare", StringComparison.Ordinal), "mentions Cloudflare");
        });

        Check("any other non JSON body is reported with its status", () =>
        {
            var handler = new FakeHandler().Queue(HttpStatusCode.BadGateway, "Bad Gateway");
            using var client = Client(handler);
            var error = Throws<TransportException>(
                () => client.SendSmsAsync("0501234567", "hi", "MyShop").GetAwaiter().GetResult());
            Expect(true, error.Message.Contains("502", StringComparison.Ordinal), "mentions 502");
        });

        Check("an empty key is refused at construction", () =>
        {
            Throws<InvalidRequestException>(() => new Meser10Client("   "));
        });

        Console.WriteLine();

        if (Failed.Count > 0)
        {
            Console.WriteLine($"{Failed.Count} failed, {_passed} passed");

            return 1;
        }

        Console.WriteLine($"{_passed} passed, 0 failed");

        return 0;
    }

    private static Meser10Client Client(FakeHandler handler) =>
        new("test-key", httpClient: new HttpClient(handler));

    private static void Check(string name, Action body)
    {
        try
        {
            body();
            _passed++;
            Console.WriteLine($"  ok    {name}");
        }
        catch (Exception exception)
        {
            Failed.Add(name);
            Console.WriteLine($"  FAIL  {name}");
            Console.WriteLine($"        {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void Expect<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new Exception($"{what}: expected {expected}, got {actual}");
        }
    }

    private static TException Throws<TException>(Action body)
        where TException : Exception
    {
        try
        {
            body();
        }
        catch (TException expected)
        {
            return expected;
        }
        catch (Exception other)
        {
            throw new Exception($"expected {typeof(TException).Name}, got {other.GetType().Name}: {other.Message}");
        }

        throw new Exception($"expected {typeof(TException).Name}, nothing was thrown");
    }
}
