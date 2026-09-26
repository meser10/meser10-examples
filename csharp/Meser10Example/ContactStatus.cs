using System.Text.Json;

namespace Meser10;

/// <summary>
/// A contact's state on the account.
///
/// The gateway returns a numeric id and a Hebrew label. Only the id is safe to
/// branch on, and the important detail is that TWO ids mean active: a contact
/// created through the API is 10, and one moved back to Active after a bounce or
/// an unsubscribe is 30. Code that checks for 10 alone silently drops every
/// reactivated contact, so use <see cref="IsMailable"/>.
/// </summary>
public sealed class ContactStatus
{
    public const int IdNotFound = 0;
    public const int IdActive = 10;
    public const int IdReactivated = 30;
    public const int IdBounced = 40;
    public const int IdUnsubscribed = 50;

    public int Id { get; }

    /// <summary>The gateway's own label, in Hebrew for the real states. Display only.</summary>
    public string Label { get; }

    public ContactStatus(int id, string label = "")
    {
        Id = id;
        Label = label;
    }

    internal static ContactStatus FromResponse(JsonElement body)
    {
        int id = IdNotFound;
        if (body.TryGetProperty("StatusID", out JsonElement statusId))
        {
            // A string is possible here, so do not assume a number.
            id = statusId.ValueKind == JsonValueKind.String
                ? int.TryParse(statusId.GetString(), out int parsed) ? parsed : IdNotFound
                : statusId.TryGetInt32(out int direct) ? direct : IdNotFound;
        }

        string label = body.TryGetProperty("Status", out JsonElement status) && status.ValueKind == JsonValueKind.String
            ? status.GetString() ?? string.Empty
            : string.Empty;

        return new ContactStatus(id, label);
    }

    public bool Exists => Id != IdNotFound;

    public bool IsMailable => Id is IdActive or IdReactivated;

    public bool IsBounced => Id == IdBounced;

    public bool IsUnsubscribed => Id == IdUnsubscribed;

    /// <summary>A stable English name for logs, independent of the gateway's label.</summary>
    public string Name => Id switch
    {
        IdNotFound => "not_found",
        IdActive => "active",
        IdReactivated => "active_reactivated",
        IdBounced => "bounced",
        IdUnsubscribed => "unsubscribed",
        _ => $"unknown_{Id}",
    };

    public override string ToString() => $"ContactStatus(Id={Id}, Name={Name})";
}

/// <summary>The status values ChangeContactStatus accepts.</summary>
public static class ContactStatusValue
{
    public const string Active = "Active";
    public const string Unsubscribed = "Unsubscribed";
    public const string Bounced = "Bounced";
}
