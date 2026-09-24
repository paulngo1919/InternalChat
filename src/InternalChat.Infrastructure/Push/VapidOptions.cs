namespace InternalChat.Infrastructure.Push;

/// <summary>
/// VAPID key pair this platform signs push requests with (research.md D10).
/// </summary>
/// <remarks>
/// Generated once per deployment (<c>web-push generate-vapid-keys</c> or equivalent) and supplied
/// at runtime — Constitution Security Requirements: "Secrets... MUST NOT appear in source,
/// <c>appsettings*.json</c>... They are supplied at runtime by the platform secret store."
/// <see cref="PrivateKey"/> is exactly such a secret.
/// </remarks>
public sealed class VapidOptions
{
    /// <summary>
    /// Configuration section name. <c>WebPush</c>, matching <c>deploy/docker-compose.yml</c>'s
    /// <c>WebPush__PublicKey</c>/<c>WebPush__PrivateKey</c>/<c>WebPush__Subject</c> and
    /// <c>deploy/.env.example</c>'s <c>VAPID_*</c> variables — wired ahead of this task at setup
    /// time (T011/T012), for the Worker host only, since it alone resolves
    /// <see cref="InternalChat.Application.Abstractions.IPushSender"/>.
    /// </summary>
    public const string SectionName = "WebPush";

    /// <summary>Base64url-encoded public key, sent to the browser when it subscribes.</summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>Base64url-encoded private key. Never logged, never returned to a client.</summary>
    public string PrivateKey { get; set; } = string.Empty;

    /// <summary>
    /// Contact the push service may use if this platform's traffic needs attention.
    /// </summary>
    /// <remarks>
    /// A <c>mailto:</c> address or an HTTPS URL, per RFC 8292. Not optional in practice: several
    /// browser vendors' push services reject VAPID requests that omit a subject.
    /// </remarks>
    public string Subject { get; set; } = string.Empty;
}
