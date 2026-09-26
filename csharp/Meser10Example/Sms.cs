using System.Text.RegularExpressions;

namespace Meser10;

public static class Sms
{
    private static readonly Regex NumericSender = new(@"^\+?[0-9]{6,19}$", RegexOptions.Compiled);
    private static readonly Regex NotAllowed = new(@"[^A-Za-z0-9 ]", RegexOptions.Compiled);
    private static readonly Regex HasLetter = new(@"[A-Za-z]", RegexOptions.Compiled);

    private static readonly Regex Gsm7Bit = new(
        @"^[A-Za-z0-9 \r\n@£$¥èéùìòÇØøÅåÆæßÉ!""#¤%&'()*+,\-./:;<=>?_¡ÄÖÑÜ§¿äöñüà^{}\[\]~|€]*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Checks a sender identity against the network rules before a request is
    /// spent on it.
    ///
    /// Two forms are accepted. An alphanumeric sender name of up to 11
    /// characters, Latin letters, digits and spaces only, containing at least
    /// one letter. Or a number, in local or E.164 form.
    ///
    /// The 11-character limit is a GSM constraint on alphanumeric sender IDs,
    /// not a Meser 10 one, and support varies by destination: alphanumeric
    /// sender IDs are not available in the United States or Canada, where a
    /// number is used instead.
    ///
    /// This checks the shape only. Whether the identity is approved on the
    /// account is decided by the gateway.
    /// </summary>
    public static void AssertSenderIsWellFormed(string from)
    {
        string value = (from ?? string.Empty).Trim();

        if (value.Length == 0)
        {
            throw new InvalidRequestException("No sender identity given.");
        }

        if (NumericSender.IsMatch(value))
        {
            return;
        }

        if (NotAllowed.IsMatch(value))
        {
            throw new InvalidRequestException(
                $"The sender name '{value}' contains characters the mobile networks reject. An "
                + "alphanumeric sender name may hold only Latin letters, digits and spaces, so "
                + "Hebrew text cannot be used as a sender name. Use an approved number instead.");
        }

        if (value.Length > 11)
        {
            throw new InvalidRequestException(
                $"The sender name '{value}' is {value.Length} characters. The limit is 11, and a "
                + "longer name is not truncated, the call is simply rejected.");
        }

        if (!HasLetter.IsMatch(value))
        {
            throw new InvalidRequestException(
                $"The sender name '{value}' has no letter in it. A digits-only value is rejected as "
                + "an alphanumeric sender name. If you meant a phone number, give the full number.");
        }
    }

    /// <summary>
    /// How many parts this text will be billed as.
    ///
    /// Any character outside the GSM 7-bit set, which includes every Hebrew
    /// letter, pushes the whole message to Unicode: 70 characters for a single
    /// part, 67 per part once it is concatenated.
    /// </summary>
    public static int Parts(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int length = text.Length;
        bool unicode = !Gsm7Bit.IsMatch(text);
        int single = unicode ? 70 : 160;
        int multi = unicode ? 67 : 153;

        return length <= single ? 1 : (int)Math.Ceiling(length / (double)multi);
    }
}
