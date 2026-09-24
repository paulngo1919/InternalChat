namespace InternalChat.Application.Abstractions;

/// <summary>
/// The VAPID public key browsers need to create a push subscription (FR-034).
/// </summary>
/// <remarks>
/// Not a secret — it is handed to every browser that subscribes, by design (RFC 8292). Split out
/// as its own narrow abstraction rather than exposing the whole VAPID configuration, so the Api
/// project can read the one value it needs without naming an Infrastructure type
/// (<c>InternalChat.Infrastructure.Push.VapidOptions</c>) in an endpoint signature.
/// </remarks>
public interface IVapidPublicKeyProvider
{
    /// <summary>Base64url-encoded public key.</summary>
    string PublicKey { get; }
}
