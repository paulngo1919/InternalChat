using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;
using Microsoft.Extensions.Options;

namespace InternalChat.Api.Authorization;

/// <summary>
/// Everything that must be true of an authenticated HTTP request before an endpoint sees it.
/// </summary>
/// <remarks>
/// <para>
/// The JWT handler proves a token is genuine, unexpired, and meant for this API. Three things it
/// cannot know follow from that and are checked here, in this order:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Has this token been revoked?</b> A signature stays valid after sign-out and after
///     deactivation. Without this check, FR-003's five-minute deadline would be met on the hub and
///     missed on every HTTP call.
///   </description></item>
///   <item><description>
///     <b>Does the subject map to an active employee?</b> A token whose subject matches no row, or
///     a deactivated one, is refused — and the two cases are distinguished in the audit log while
///     being identical in the response.
///   </description></item>
///   <item><description>
///     <b>Is this session known?</b> First sight registers it and emits exactly one
///     <c>auth.signin</c> audit event (FR-006). Auditing per request instead would produce a log
///     nobody can read, which SC-021 needs to be readable a year later.
///   </description></item>
/// </list>
/// <para>
/// Placed after <c>UseAuthentication</c> and before <c>UseAuthorization</c>, so it sees a populated
/// principal and runs before any endpoint's policy. Anonymous requests pass straight through —
/// health probes must not need Redis to answer.
/// </para>
/// </remarks>
public sealed class AccessGateMiddleware : IMiddleware
{
    private readonly IRevocationStore _revocations;
    private readonly IEmployeeDirectory _directory;
    private readonly ISessionRegistry _sessions;
    private readonly ISecurityAuditor _auditor;
    private readonly CurrentEmployee _currentEmployee;
    private readonly ChatAuthenticationOptions _options;

    /// <summary>Creates the gate.</summary>
    public AccessGateMiddleware(
        IRevocationStore revocations,
        IEmployeeDirectory directory,
        ISessionRegistry sessions,
        ISecurityAuditor auditor,
        CurrentEmployee currentEmployee,
        IOptions<ChatAuthenticationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(revocations);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(auditor);
        ArgumentNullException.ThrowIfNull(currentEmployee);
        ArgumentNullException.ThrowIfNull(options);

        _revocations = revocations;
        _directory = directory;
        _sessions = sessions;
        _auditor = auditor;
        _currentEmployee = currentEmployee;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        string? subject = ChatClaims.SubjectOf(context.User);

        if (context.User.Identity?.IsAuthenticated != true || subject is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        string? sessionId = ChatClaims.SessionIdOf(context.User);
        string? sourceIp = context.Connection.RemoteIpAddress?.ToString();

        if (await _revocations.IsRevokedAsync(subject, sessionId, context.RequestAborted).ConfigureAwait(false))
        {
            await RefuseAsync(context, null, "token_revoked", sourceIp).ConfigureAwait(false);
            return;
        }

        EmployeeProfile? employee = await _directory
            .FindBySubjectAsync(subject, context.RequestAborted)
            .ConfigureAwait(false);

        if (employee is null)
        {
            // A validly signed token for somebody this platform has never heard of. Worth its own
            // audit reason: it usually means directory sync is behind, and occasionally means
            // something considerably more interesting.
            await RefuseAsync(context, null, "unknown_subject", sourceIp).ConfigureAwait(false);
            return;
        }

        if (!employee.IsActive)
        {
            await RefuseAsync(context, employee.Id, "employee_deactivated", sourceIp).ConfigureAwait(false);
            return;
        }

        _currentEmployee.Set(employee, sessionId);

        if (sessionId is not null)
        {
            bool firstSight = await _sessions
                .TouchAsync(
                    employee.Id,
                    sessionId,
                    context.Request.Headers.UserAgent.ToString(),
                    _options.MaxAccessTokenLifetime + TimeSpan.FromHours(1),
                    context.RequestAborted)
                .ConfigureAwait(false);

            if (firstSight)
            {
                await _auditor
                    .SignInAsync(employee.Id, sessionId, sourceIp, context.RequestAborted)
                    .ConfigureAwait(false);
            }
        }

        await next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses with 401 and records why.
    /// </summary>
    /// <remarks>
    /// 401 rather than 403 across all three cases, and with no body. The distinction between "your
    /// token was revoked", "we do not know you", and "you were deactivated" is recorded in the
    /// audit log, where an administrator can act on it, and withheld from the response, where it
    /// would only tell the holder of a token which of their assumptions to change.
    /// </remarks>
    private async Task RefuseAsync(HttpContext context, Guid? employeeId, string reason, string? sourceIp)
    {
        await _auditor
            .AccessDeniedAsync(employeeId, "employee", employeeId, reason, sourceIp, context.RequestAborted)
            .ConfigureAwait(false);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }
}
