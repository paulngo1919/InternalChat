namespace InternalChat.Api.Contracts;

/// <summary>Request to claim the screen-share slot (FR-048).</summary>
/// <param name="Scope">
/// <c>screen</c> or <c>window</c>. Recorded rather than inferred because FR-048 makes a promise
/// about the window case specifically, and an audit record that could not tell them apart could
/// not evidence it.
/// </param>
public sealed record StartScreenShareRequest(string Scope);

/// <summary>The outcome of claiming the slot.</summary>
/// <param name="DisplacedEmployeeId">
/// Whose share this one stopped, or <c>null</c> when the slot was free.
///
/// <b>This field is FR-050's "visible".</b> The rule is last-writer-wins, and a takeover that told
/// nobody would leave the displaced presenter unable to distinguish it from their connection
/// dropping.
/// </param>
public sealed record ScreenShareResponse(
    Guid MeetingId,
    Guid EmployeeId,
    string Scope,
    DateTimeOffset StartedAt,
    Guid? DisplacedEmployeeId);

/// <summary>The screen-share scope spellings the API uses. Lowercase, not the C# member names.</summary>
public static class ShareScopes
{
    /// <summary>The whole screen, including anything else on it.</summary>
    public const string Screen = "screen";

    /// <summary>A single application window. Nothing outside it is captured (FR-048).</summary>
    public const string Window = "window";
}
