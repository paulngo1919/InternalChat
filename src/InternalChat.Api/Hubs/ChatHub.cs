using InternalChat.Api.Authorization;
using InternalChat.Api.Contracts;
using InternalChat.Api.Mapping;
using InternalChat.Application.Abstractions;
using InternalChat.Application.Authorization;
using InternalChat.Application.Behaviors;
using InternalChat.Application.Messages;
using InternalChat.Domain.Messages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.SignalR;

namespace InternalChat.Api.Hubs;

/// <summary>
/// The real-time hub (<c>contracts/signalr-hub.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>No business logic, by rule.</b> Constitution Principle I requires every method to delegate to
/// an Application use case, and T081 fails the build if a method appears here that the contract does
/// not document. What the hub owns is transport concerns: group membership, and turning a domain
/// object into the DTO the client expects.
/// </para>
/// <para>
/// <b>Messages are not sent over the hub</b> and there is deliberately no method to do so. The
/// idempotent send path (FR-011) lives on HTTP, where 201 and 200 can express "created" and "you
/// already sent this". A hub-side send would duplicate retry semantics onto a transport with no
/// status codes to say the second thing, and a client could not tell a lost message from a
/// successful retry.
/// </para>
/// <para>
/// <b>Authentication is checked three times, for three different reasons.</b>
/// <see cref="AuthorizeAttribute"/> covers the connect; <see cref="HubAuthorizationFilter"/>
/// re-checks the revocation set on every invocation, because a long-lived connection must not
/// outlive its token; and <see cref="RevocationSweepService"/> closes idle connections, because a
/// client that invokes nothing is exactly the case the filter cannot see (FR-003, SC-018).
/// </para>
/// </remarks>
[Authorize]
public sealed class ChatHub : Hub
{
    /// <summary>Path this hub is mapped at.</summary>
    public const string Path = AuthenticationExtensions.HubPath;

    private readonly IUseCaseDispatcher _dispatcher;
    private readonly IConversationReader _conversations;
    private readonly IEmployeeDirectory _directory;
    private readonly IConversationMembershipEvaluator _membership;
    private readonly IPresenceStore _presence;
    private readonly IDeliveryMetrics _metrics;

    /// <summary>Key under which the connection's transport name is kept for its disconnect.</summary>
    private const string TransportItem = "internalchat.transport";

    /// <summary>Creates the hub.</summary>
    public ChatHub(
        IUseCaseDispatcher dispatcher,
        IConversationReader conversations,
        IEmployeeDirectory directory,
        IConversationMembershipEvaluator membership,
        IPresenceStore presence,
        IDeliveryMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(presence);
        ArgumentNullException.ThrowIfNull(metrics);

        _dispatcher = dispatcher;
        _conversations = conversations;
        _directory = directory;
        _membership = membership;
        _presence = presence;
        _metrics = metrics;
    }

    /// <summary>The SignalR group every member of a conversation is placed in.</summary>
    /// <remarks>
    /// One group per conversation, named exactly as <c>contracts/signalr-hub.md</c> specifies. The
    /// fan-out consumer publishes to this name, so a change here silently stops delivery rather than
    /// failing — which is why the name is derived in one place instead of formatted at each call.
    /// </remarks>
    public static string GroupFor(Guid conversationId) => $"conv:{conversationId}";

    /// <summary>
    /// Joins the caller to a group per active conversation membership.
    /// </summary>
    /// <remarks>
    /// Done on connect rather than lazily, because a message can arrive before the client asks for
    /// anything. A connection not yet in the group would miss it and only discover the gap at its
    /// next <see cref="Resync"/> — which is recoverable, but means real-time delivery silently
    /// degrades to polling for the first few seconds of every connection.
    /// </remarks>
    public override async Task OnConnectedAsync()
    {
        Guid employeeId = await ResolveEmployeeAsync().ConfigureAwait(false);

        // 002 FR-010: tell the client which transport it actually got. First, so the indicator is
        // right before any message arrives; to the caller only, and it carries nothing else.
        string transport = TransportName(Context.Features.Get<IHttpTransportFeature>()?.TransportType);
        Context.Items[TransportItem] = transport;
        _metrics.ConnectionOpened(transport);

        await Clients.Caller
            .SendAsync(ChatHubEvents.ConnectionInfo, new { transport }, Context.ConnectionAborted)
            .ConfigureAwait(false);

        IReadOnlyList<ConversationSummary> reachable = await _conversations
            .ListForEmployeeAsync(employeeId, ResyncConversationsHandler.MaximumConversations, null, Context.ConnectionAborted)
            .ConfigureAwait(false);

        foreach (ConversationSummary summary in reachable)
        {
            await Groups
                .AddToGroupAsync(Context.ConnectionId, GroupFor(summary.Conversation.Id), Context.ConnectionAborted)
                .ConfigureAwait(false);
        }

        // Presence is asserted on connect rather than waiting for the client to say so, because a
        // client that connected and then crashed before sending anything would otherwise never
        // appear online at all. The TTL means it decays without a refresh either way.
        await _presence
            .SetPresenceAsync(employeeId, PresenceState.Online, Context.ConnectionAborted)
            .ConfigureAwait(false);

        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    /// <summary>Counts the connection out of its transport's gauge.</summary>
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(TransportItem, out object? transport) && transport is string name)
        {
            _metrics.ConnectionClosed(name);
        }

        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>The contract's name for a negotiated transport (hub contract 1.1.0).</summary>
    /// <remarks>
    /// Server-sent events is reported as long polling: the contract has two states because the
    /// client shows one indicator, and either fallback means "not WebSockets, messages may lag".
    /// </remarks>
    private static string TransportName(HttpTransportType? transport) =>
        transport == HttpTransportType.WebSockets ? "webSockets" : "longPolling";

    /// <summary>
    /// Returns everything above each conversation's last seen sequence (FR-018, SC-022).
    /// </summary>
    /// <remarks>
    /// Authorization is per entry and inside the use case, because a stale client routinely names a
    /// conversation it was just removed from. Refusing the whole batch would make one removal look
    /// like a total outage; the entry is omitted instead, indistinguishable from a conversation that
    /// never existed (SC-017).
    /// </remarks>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<MessageResponse>>> Resync(
        Dictionary<Guid, long> lastSeenSeq)
    {
        ArgumentNullException.ThrowIfNull(lastSeenSeq);

        Guid employeeId = await ResolveEmployeeAsync().ConfigureAwait(false);

        ResyncResult result = await _dispatcher
            .SendAsync<ResyncConversations, ResyncResult>(
                new ResyncConversations(employeeId, lastSeenSeq),
                Context.ConnectionAborted)
            .ConfigureAwait(false);

        Dictionary<Guid, IReadOnlyList<MessageResponse>> mapped = [];

        foreach ((Guid conversationId, IReadOnlyList<Message> messages) in result.Missed)
        {
            mapped[conversationId] = [.. messages.Select(m => m.ToResponse())];

            // A client that reconnects into a conversation it joined while away would otherwise
            // receive the catch-up and then no live messages, because the connect-time group join
            // already happened.
            await Groups
                .AddToGroupAsync(Context.ConnectionId, GroupFor(conversationId), Context.ConnectionAborted)
                .ConfigureAwait(false);
        }

        return mapped;
    }

    /// <summary>Marks the caller as typing in a conversation (FR-016).</summary>
    /// <remarks>
    /// Fire-and-forget by design, and short-lived. The client re-asserts while the person keeps
    /// typing; stopping is the absence of a re-assertion, so a dropped connection resolves itself
    /// rather than leaving a permanent indicator.
    /// </remarks>
    public async Task StartTyping(Guid conversationId)
    {
        Guid employeeId = await RequireMembershipAsync(conversationId).ConfigureAwait(false);

        await _presence
            .StartTypingAsync(conversationId, employeeId, Context.ConnectionAborted)
            .ConfigureAwait(false);

        await BroadcastTypingAsync(conversationId).ConfigureAwait(false);
    }

    /// <summary>Clears the caller's typing marker early (FR-016).</summary>
    public async Task StopTyping(Guid conversationId)
    {
        Guid employeeId = await RequireMembershipAsync(conversationId).ConfigureAwait(false);

        await _presence
            .StopTypingAsync(conversationId, employeeId, Context.ConnectionAborted)
            .ConfigureAwait(false);

        await BroadcastTypingAsync(conversationId).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the caller's own presence (FR-017).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Self only — the employee id comes from the connection's identity, so there is no id to
    /// substitute in order to set a colleague's presence.
    /// </para>
    /// <para>
    /// <c>offline</c> is refused rather than accepted. Offline is inferred from the absence of a
    /// presence key, and letting a client assert it would create two different meanings for the same
    /// state: one that expires and one that does not.
    /// </para>
    /// </remarks>
    public async Task SetPresence(string presence)
    {
        Guid employeeId = await ResolveEmployeeAsync().ConfigureAwait(false);

        PresenceState state = presence switch
        {
            "online" => PresenceState.Online,
            "away" => PresenceState.Away,
            "dnd" => PresenceState.DoNotDisturb,
            _ => throw new HubException(
                $"'{presence}' is not a presence state a client may set. Use 'online', 'away', or "
                + "'dnd' — 'offline' is inferred from disconnection and cannot be asserted."),
        };

        await _presence.SetPresenceAsync(employeeId, state, Context.ConnectionAborted).ConfigureAwait(false);

        await Clients
            .Others
            .SendAsync(
                ChatHubEvents.PresenceChanged,
                new { employeeId, presence },
                Context.ConnectionAborted)
            .ConfigureAwait(false);
    }

    /// <summary>Tells the conversation who is currently typing.</summary>
    /// <remarks>
    /// The whole set is sent rather than a delta. A client that missed one event would otherwise
    /// hold a wrong list indefinitely, and the set is at most a handful of ids.
    /// </remarks>
    private async Task BroadcastTypingAsync(Guid conversationId)
    {
        IReadOnlyList<Guid> typing = await _presence
            .GetTypingAsync(conversationId, Context.ConnectionAborted)
            .ConfigureAwait(false);

        await Clients
            .Group(GroupFor(conversationId))
            .SendAsync(
                ChatHubEvents.TypingChanged,
                new { conversationId, employeeIds = typing },
                Context.ConnectionAborted)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the caller's internal employee id from the connection's token.
    /// </summary>
    /// <remarks>
    /// <see cref="CurrentEmployee"/> is not usable here: it is populated by
    /// <see cref="AccessGateMiddleware"/>, which runs for HTTP requests, and a hub invocation gets
    /// its own scope where it would be empty. Resolving from the <c>sub</c> claim reaches the same
    /// answer through the same directory lookup.
    /// </remarks>
    private async Task<Guid> ResolveEmployeeAsync()
    {
        string subject = ChatClaims.SubjectOf(Context.User)
            ?? throw new HubException("This connection carries no subject.");

        EmployeeProfile profile = await _directory
            .FindBySubjectAsync(subject, Context.ConnectionAborted)
            .ConfigureAwait(false)
            ?? throw new HubException("No employee matches this connection.");

        if (!profile.IsActive)
        {
            // The sweep closes idle connections within five minutes, and the filter catches an
            // invocation once the revocation entry lands. This is the third case: a deactivation
            // that has not yet reached Redis, on a connection that is actively invoking.
            throw new HubException("This employee is no longer active.");
        }

        return profile.Id;
    }

    /// <summary>
    /// Resolves the caller and refuses if they are not a member of the conversation.
    /// </summary>
    /// <remarks>
    /// The hub equivalent of <see cref="MembershipEndpointFilter"/>, and required for the same
    /// reason: a hub method takes a conversation id as a parameter, so without this check any
    /// connected employee could make a colleague appear to be typing in a conversation they cannot
    /// see — and learn that it exists.
    /// </remarks>
    private async Task<Guid> RequireMembershipAsync(Guid conversationId)
    {
        Guid employeeId = await ResolveEmployeeAsync().ConfigureAwait(false);

        MembershipDecision decision = await _membership
            .EvaluateAsync(
                employeeId,
                new ConversationMembershipRequirement(conversationId),
                Context.ConnectionAborted)
            .ConfigureAwait(false);

        // A bare "not authorized" rather than a reason. The message reaches the client, and naming
        // the conversation or distinguishing "not a member" from "does not exist" would be the same
        // enumeration oracle SC-017 closes on the HTTP side.
        return decision.IsAllowed
            ? employeeId
            : throw new HubException("Not authorized.");
    }
}
