namespace InternalChat.Api.Authorization;

/// <summary>
/// How this API validates Keycloak tokens, and how quickly it stops honouring them.
/// </summary>
/// <remarks>
/// Bound to the <c>Keycloak</c> section, which is what <c>deploy/docker-compose.yml</c> already
/// supplies as <c>Keycloak__Authority</c> and friends.
/// </remarks>
public sealed class ChatAuthenticationOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Keycloak";

    /// <summary>
    /// Realm URL, for example <c>http://keycloak:8080/realms/internalchat</c>.
    /// </summary>
    /// <remarks>
    /// Also the expected <c>iss</c> claim. Keycloak derives the issuer it stamps from the address
    /// the request arrived on, so a token minted through a different spelling of the same host is
    /// rejected here for issuer mismatch — which surfaces as a signature-shaped error and is not one.
    /// </remarks>
    public string Authority { get; set; } = string.Empty;

    /// <summary>
    /// Required <c>aud</c> claim.
    /// </summary>
    /// <remarks>
    /// Keycloak does not put a client in its own token's audience by default — it sets <c>azp</c>
    /// instead — so the realm carries an explicit audience mapper. Without validating audience, a
    /// token minted for any other client in the realm would be accepted here.
    /// </remarks>
    public string Audience { get; set; } = string.Empty;

    /// <summary>OIDC client id of the SPA.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Client secret used to authenticate back-channel logout callbacks.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Whether OIDC metadata must be fetched over HTTPS. Defaults to <c>true</c>.
    /// </summary>
    /// <remarks>
    /// A default of <c>true</c> means turning it off is a visible, deliberate line in a
    /// configuration file — which in this repository happens in exactly two places, both of which
    /// talk to a container over loopback.
    /// </remarks>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// Longest an access token can live, in seconds. Must match the realm (research.md D5).
    /// </summary>
    /// <remarks>
    /// Not used to validate anything — <c>exp</c> does that. It is how long a revocation entry has
    /// to survive: an entry that expired before the token it revokes would let a revoked token
    /// start working again, which is a strictly worse failure than never having revoked it.
    /// </remarks>
    public int MaxAccessTokenLifetimeSeconds { get; set; } = 300;

    /// <summary>
    /// How often open hub connections are re-checked against the revocation set, in seconds.
    /// </summary>
    /// <remarks>
    /// FR-003 allows five minutes. Thirty seconds leaves an order of magnitude of margin for
    /// directory-sync propagation, and the sweep is a Redis lookup per open connection — cheap
    /// enough at 7,000 connections to run far more often than the budget demands.
    /// </remarks>
    public int RevocationSweepSeconds { get; set; } = 30;

    /// <summary>Revocation entry lifetime.</summary>
    public TimeSpan MaxAccessTokenLifetime => TimeSpan.FromSeconds(MaxAccessTokenLifetimeSeconds);

    /// <summary>Interval between revocation sweeps of open connections.</summary>
    public TimeSpan RevocationSweep => TimeSpan.FromSeconds(RevocationSweepSeconds);
}
