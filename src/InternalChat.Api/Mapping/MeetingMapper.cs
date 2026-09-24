using InternalChat.Api.Contracts;
using InternalChat.Domain.Meetings;

namespace InternalChat.Api.Mapping;

/// <summary>Turns meetings into the wire contract.</summary>
/// <remarks>
/// <c>tests/Architecture/BoundaryTests.cs</c> forbids a Domain type reaching an Api contract, and
/// this is where that is honoured for meetings. Participations are deliberately not projected: a
/// meeting response reports a <em>count</em>, and listing who is in a call is a separate question
/// with its own authorization, not something to leak into every start response.
/// </remarks>
public static class MeetingMapper
{
    /// <summary>Maps a meeting.</summary>
    public static MeetingResponse ToResponse(this Meeting meeting)
    {
        ArgumentNullException.ThrowIfNull(meeting);

        return new MeetingResponse(
            meeting.Id,
            meeting.ConversationId,
            meeting.StartedBy,
            meeting.StartedAt,
            meeting.EndedAt,
            meeting.ActiveParticipantCount,
            Meeting.MaximumParticipants);
    }
}
