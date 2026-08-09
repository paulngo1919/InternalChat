using InternalChat.Application.Abstractions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace InternalChat.Api.Authorization;

/// <summary>
/// T063 — validates the token on connect <b>and</b> on every hub invocation.
/// </summary>
/// <remarks>
/// <para>
/// contracts/signalr-hub.md: "JWT re-validated and the revocation set re-checked — a long-lived
/// connection MUST NOT outlive its token (D5)." <c>[Authorize]</c> on the hub only covers the
/// first of those. It runs once, during the negotiate, and never again for the life of the socket —
/// which for a chat client is hours.
/// </para>
/// <para>
/// So this filter re-checks two things every time the connection is used: that the token has not
/// expired, and that neither the session nor the subject has been revoked. Between them they cover
/// every way access can end while a socket stays open.
/// </para>
/// <para>
/// What this filter cannot do is close an <em>idle</em> connection — it only runs when the client
/// invokes something, and a client that has connected and is waiting for messages invokes nothing
/// at all. That case is <see cref="RevocationSweepService"/>'s, and it is the case SC-018 tests.
/// The two mechanisms are not redundant: the filter is immediate but event-driven, the sweep is
/// periodic but unconditional.
/// </para>
/// </remarks>
public sealed partial class HubAuthorizationFilter : IHubFilter
{
    private readonly IRevocationStore _revocations;
    private readonly HubConnectionRegistry _registry;
    private readonly ILogger<HubAuthorizationFilter> _logger;

    /// <summary>Creates the filter.</summary>
    public HubAuthorizationFilter(
        IRevocationStore revocations,
        HubConnectionRegistry registry,
        ILogger<HubAuthorizationFilter> logger)
    {
        ArgumentNullException.ThrowIfNull(revocations);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);

        _revocations = revocations;
        _registry = registry;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (!await IsStillValidAsync(context.Context).ConfigureAwait(false))
        {
            // Abort rather than throw. A thrown exception here is reported to the client as a hub
            // error while the transport stays up, which is not what "refused" means.
            context.Context.Abort();
            return;
        }

        if (!_registry.TryAdd(context.Context, context.Context.User))
        {
            UnidentifiedConnectionRefused(_logger, context.Context.ConnectionId);
            context.Context.Abort();
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task OnDisconnectedAsync(
        HubLifetimeContext context,
        Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        _registry.Remove(context.Context.ConnectionId);

        await next(context, exception).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        ArgumentNullException.ThrowIfNull(invocationContext);
        ArgumentNullException.ThrowIfNull(next);

        if (!await IsStillValidAsync(invocationContext.Context).ConfigureAwait(false))
        {
            invocationContext.Context.Abort();

            // The caller gets a refusal that says nothing about why. An "expired token" message
            // here would be helpful, and would also tell an attacker holding a stolen token
            // precisely which of their assumptions was wrong.
            throw new HubException("Not authorized.");
        }

        return await next(invocationContext).ConfigureAwait(false);
    }

    /// <summary>Whether this connection's credentials still stand.</summary>
    internal async Task<bool> IsStillValidAsync(HubCallerContext context)
    {
        string? subject = ChatClaims.SubjectOf(context.User);
        if (subject is null)
        {
            return false;
        }

        DateTimeOffset? expiresAt = ChatClaims.ExpiresAtOf(context.User);
        if (expiresAt is null || expiresAt <= DateTimeOffset.UtcNow)
        {
            TokenExpiredOnOpenConnection(_logger, subject);
            return false;
        }

        string? sessionId = ChatClaims.SessionIdOf(context.User);

        if (await _revocations.IsRevokedAsync(subject, sessionId, context.ConnectionAborted)
                .ConfigureAwait(false))
        {
            RevokedOnOpenConnection(_logger, subject);
            return false;
        }

        return true;
    }

    [LoggerMessage(
        EventId = 3200,
        Level = LogLevel.Warning,
        Message = "Hub connection {ConnectionId} carried no subject claim and was refused")]
    private static partial void UnidentifiedConnectionRefused(ILogger logger, string connectionId);

    [LoggerMessage(
        EventId = 3201,
        Level = LogLevel.Information,
        Message = "Closing hub connection for {Subject}: the access token has expired")]
    private static partial void TokenExpiredOnOpenConnection(ILogger logger, string subject);

    [LoggerMessage(
        EventId = 3202,
        Level = LogLevel.Information,
        Message = "Closing hub connection for {Subject}: the session or subject has been revoked")]
    private static partial void RevokedOnOpenConnection(ILogger logger, string subject);
}
