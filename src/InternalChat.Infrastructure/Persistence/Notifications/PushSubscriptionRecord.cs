namespace InternalChat.Infrastructure.Persistence.Notifications;

/// <summary>
/// One browser's push registration (data-model.md, <c>push_subscription</c>).
/// </summary>
/// <remarks>
/// A plain persistence record rather than a domain type: no invariant attaches to a subscription
/// beyond "the endpoint is unique" and "a rejected one is deleted", both of which are storage
/// concerns the repository enforces directly — the same reasoning that keeps
/// <see cref="Messages.MessageDeduplicationRecord"/> out of the domain.
/// </remarks>
public sealed class PushSubscriptionRecord
{
    /// <summary>Our identity for the subscription.</summary>
    public Guid Id { get; set; }

    /// <summary>Who registered it.</summary>
    public Guid EmployeeId { get; set; }

    /// <summary>The browser vendor's push endpoint. Unique — see <see cref="RegisterAsync"/>'s remarks.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Client public key (VAPID/RFC 8291).</summary>
    public string P256dh { get; set; } = string.Empty;

    /// <summary>Client auth secret (VAPID/RFC 8291).</summary>
    public string Auth { get; set; } = string.Empty;

    /// <summary>Shown in a future device list. Best-effort; browsers may omit or fake it.</summary>
    public string? UserAgent { get; set; }

    /// <summary>When this platform first saw it.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When a push last succeeded against it, or <c>null</c> if never.</summary>
    public DateTimeOffset? LastSuccessAt { get; set; }
}
