namespace Meser10;

/// <summary>
/// Base class, so one catch covers everything this client throws.
/// </summary>
public class Meser10Exception : Exception
{
    public Meser10Exception(string message) : base(message) { }

    public Meser10Exception(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Refused here, before the network. Nothing was sent and nothing was spent.
/// </summary>
public sealed class InvalidRequestException : Meser10Exception
{
    public InvalidRequestException(string message) : base(message) { }
}

/// <summary>
/// ErrorCode 1: the key was rejected.
///
/// Catch this to stop, log and alert. Never to retry. Repeated authentication
/// failures block the calling IP address for several hours, and the block is on
/// the address rather than the key, so a retry loop takes down every other
/// integration sending from the same host.
/// </summary>
public sealed class AuthenticationException : Meser10Exception
{
    public AuthenticationException(string message) : base(message) { }
}

/// <summary>No readable answer: network, timeout, or Cloudflare.</summary>
public sealed class TransportException : Meser10Exception
{
    public TransportException(string message) : base(message) { }

    public TransportException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Any other ErrorCode. <see cref="GatewayMessage"/> arrives in Hebrew on most
/// failures, so show your own wording to users and log this one.
/// </summary>
public sealed class ApiException : Meser10Exception
{
    /// <summary>Kept as a string because the gateway varies its type between functions.</summary>
    public string ErrorCode { get; }

    public string GatewayMessage { get; }

    public string Function { get; }

    /// <summary>True when a single retry is reasonable. Never true for a key failure.</summary>
    public bool IsTransient => ErrorCode == "3";

    private ApiException(string errorCode, string gatewayMessage, string function, string message)
        : base(message)
    {
        ErrorCode = errorCode;
        GatewayMessage = gatewayMessage;
        Function = function;
    }

    public static ApiException FromCode(string code, string gatewayMessage, string function)
    {
        string advice = code switch
        {
            "3" => "An application error, either on the gateway or a payload it could not process "
                 + "at all. Safe to try once more; if it persists, send support the tracking id in "
                 + "the gateway message.",
            "4" => "A parameter was missing or invalid. On CreateContact this most often means the "
                 + "named list does not exist on this account, and on an SMS it most often means "
                 + "the sender identity is not approved yet. The gateway message names which.",
            "6" => "The gateway does not know this function name.",
            _ => "An error code this client does not recognise.",
        };

        return new ApiException(
            code,
            gatewayMessage,
            function,
            $"{function} failed with ErrorCode {code}. {advice} Gateway said: {gatewayMessage}");
    }
}
