using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;
using InternalChat.Domain.Conversations;
using InternalChat.Domain.Messages;

namespace InternalChat.Application.Messages;

/// <summary>
/// Catches a reconnecting client up (FR-018, SC-022).
/// </summary>
/// <param name="LastSeenSeq">
/// Per conversation, the highest sequence the client has already applied. Zero means "I have
/// nothing" — sequences start at one, so there is no ambiguity with an actual message.
/// </param>
public sealed record ResyncConversations(
    Guid EmployeeId,
    IReadOnlyDictionary<Guid, long> LastSeenSeq);

/// <summary>What the client missed, per conversation.</summary>
public sealed record ResyncResult(IReadOnlyDictionary<Guid, IReadOnlyList<Message>> Missed);

/// <summary>
/// Returns everything above each conversation's floor, bounded per conversation.
/// </summary>
/// <remarks>
/// <para>
/// <b>This method is why the hub can be disposable.</b> Nothing is buffered per connection —
/// buffering would make server memory a function of how many clients are disconnected, which breaks
/// at 7,000 connections — so a reconnecting client recovers its gap with a query. Every "a message is
/// never lost" claim in the spec ultimately rests here.
/// </para>
/// <para>
/// <b>Authorization is per entry, and a failure is an omission rather than an error.</b> A stale
/// client will routinely name a conversation it was just removed from; throwing would break the
/// reconnect for every <em>other</em> conversation in the same batch, so removal would look like a
/// total outage to the person it happened to. An omitted conversation is also indistinguishable from
/// one that never existed, which is what SC-017 requires.
/// </para>
/// <para>
/// <b>Bounded per conversation.</b> A laptop offline for a month would otherwise ask for tens of
/// thousands of rows in one hub invocation, on a connection with no backpressure. The cap is applied
/// from the oldest end so the client can page forward by raising its floor; capping from the newest
/// end would leave a hole in the middle that the client has no way to name.
/// </para>
/// </remarks>
public sealed class ResyncConversationsHandler : IUseCase<ResyncConversations, ResyncResult>
{
    /// <summary>Most messages returned per conversation in one invocation.</summary>
    /// <remarks>
    /// Matches the history endpoint's maximum page. A client that receives exactly this many should
    /// assume there is more and ask again with a raised floor — which is the same paging it already
    /// does for history, rather than a second mechanism to implement.
    /// </remarks>
    public const int MaximumPerConversation = GetHistoryHandler.MaximumLimit;

    /// <summary>
    /// Most conversations accepted in one batch.
    /// </summary>
    /// <remarks>
    /// A cap on the fan-out of the whole call, not on any one conversation. Without it a client could
    /// request a hundred bounded pages at once and turn a bounded query into an unbounded request.
    /// </remarks>
    public const int MaximumConversations = 200;

    private readonly IMessageRepository _messages;
    private readonly IMembershipRepository _memberships;

    /// <summary>Creates the handler.</summary>
    public ResyncConversationsHandler(IMessageRepository messages, IMembershipRepository memberships)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(memberships);

        _messages = messages;
        _memberships = memberships;
    }

    /// <inheritdoc />
    public async Task<ResyncResult> HandleAsync(
        ResyncConversations request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Dictionary<Guid, IReadOnlyList<Message>> missed = [];

        foreach ((Guid conversationId, long lastSeenSeq) in request.LastSeenSeq.Take(MaximumConversations))
        {
            Membership? membership = await _memberships
                .FindAsync(conversationId, request.EmployeeId, cancellationToken)
                .ConfigureAwait(false);

            if (membership is null || !membership.IsActive)
            {
                // Omitted, not reported. See the class remarks: a stale entry must not fail the
                // batch, and must look the same as a conversation that does not exist.
                continue;
            }

            // The floor is the higher of what the client claims to have and what it is allowed to
            // see. Taking the client's number alone would let a removed-and-re-added member recover
            // history below their new floor simply by asking for it.
            long floor = Math.Max(lastSeenSeq, membership.VisibleFromSeq);

            IReadOnlyList<Message> page = await _messages
                .GetHistoryAsync(
                    new MessageHistoryQuery(
                        conversationId,
                        membership.VisibleFromSeq,
                        BeforeSeq: null,
                        AfterSeq: floor,
                        MaximumPerConversation),
                    cancellationToken)
                .ConfigureAwait(false);

            missed[conversationId] = page;
        }

        return new ResyncResult(missed);
    }
}
