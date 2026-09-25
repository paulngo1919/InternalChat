using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using InternalChat.Api.Authorization;
using InternalChat.Application.Abstractions;
using InternalChat.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace InternalChat.Api.Endpoints;

/// <summary>
/// Platform administration endpoints (T207).
/// </summary>
public static class AdminEndpoints
{
    /// <summary>Maps the /admin endpoints.</summary>
    public static void MapAdminEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/admin")
            .RequireAuthorization(AuthorizationPolicies.PlatformAdmin)
            .WithTags("Admin");

        // FR-053: Retention policy is visible to all employees
        routes.MapGet("/admin/retention", GetRetentionPolicy)
            .RequireAuthorization(AuthorizationPolicies.Employee)
            .WithTags("Admin")
            .WithName("GetRetentionPolicy")
            .WithSummary("Current retention policy (FR-053)");

        group.MapPut("/retention", UpdateRetentionPolicy)
            .WithName("UpdateRetentionPolicy")
            .WithSummary("Change the retention period (FR-054). Audited.");
            
        group.MapPost("/exports", RequestExport)
            .WithName("RequestExport")
            .WithSummary("Request an export of a conversation or an employee's content (FR-055). Audited.");
    }

    private static IResult GetRetentionPolicy(
        IConfiguration configuration,
        IClock clock)
    {
        int months = configuration.GetValue<int>("Retention:Months", 12);
        
        DateTimeOffset now = clock.UtcNow;
        DateTimeOffset appliesFrom = now.AddMonths(-Math.Max(1, months));
        
        // Next sweep is effectively tomorrow at midnight UTC, since the Worker runs a daily timer.
        DateTimeOffset nextSweepAt = new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);

        return Results.Ok(new RetentionPolicyResponse(months, appliesFrom, nextSweepAt));
    }

    private static async Task<IResult> UpdateRetentionPolicy(
        [FromBody] RetentionPolicyRequest request,
        HttpContext httpContext,
        IConfiguration configuration,
        CurrentEmployee employee,
        IAuditLog auditLog,
        IClock clock,
        IWebHostEnvironment env,
        CancellationToken cancellationToken)
    {
        if (request.RetentionMonths < 1)
        {
            return Results.BadRequest(new ProblemDetails
            {
                Type = "urn:internalchat:bad-request",
                Title = "Invalid retention policy",
                Detail = "Retention months must be at least 1."
            });
        }

        int oldMonths = configuration.GetValue<int>("Retention:Months", 12);

        // FR-054: Change the configuration without a code deployment. We write back to appsettings.json.
        // IOptionsMonitor (used by RetentionSweepJob) reloads automatically when the file changes.
        string path = Path.Combine(env.ContentRootPath, "appsettings.json");
        if (File.Exists(path))
        {
            string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var node = JsonNode.Parse(json, new JsonNodeOptions { PropertyNameCaseInsensitive = true }) as JsonObject;
            if (node != null)
            {
                if (node["Retention"] is JsonObject retentionNode)
                {
                    retentionNode["Months"] = request.RetentionMonths;
                }
                else
                {
                    node["Retention"] = new JsonObject
                    {
                        ["Months"] = request.RetentionMonths
                    };
                }

                await File.WriteAllTextAsync(
                    path, 
                    node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), 
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await auditLog.RecordAsync(new AuditEntry(
            Action: "retention.policy.changed",
            ActorId: employee.Id,
            SubjectType: "retention",
            SubjectId: null,
            SourceIp: httpContext.Connection.RemoteIpAddress?.ToString(),
            Outcome: AuditOutcome.Success,
            Detail: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["oldMonths"] = oldMonths.ToString(CultureInfo.InvariantCulture),
                ["newMonths"] = request.RetentionMonths.ToString(CultureInfo.InvariantCulture)
            }
        ), cancellationToken).ConfigureAwait(false);

        DateTimeOffset now = clock.UtcNow;
        DateTimeOffset appliesFrom = now.AddMonths(-Math.Max(1, request.RetentionMonths));
        DateTimeOffset nextSweepAt = new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);

        return Results.Ok(new RetentionPolicyResponse(request.RetentionMonths, appliesFrom, nextSweepAt));
    }

    private static async Task<IResult> RequestExport(
        [FromBody] ExportRequest request,
        HttpContext httpContext,
        CurrentEmployee employee,
        IEventPublisher eventPublisher,
        IAuditLog auditLog,
        IUnitOfWork uow,
        CancellationToken cancellationToken)
    {
        if (request.ConversationId == null && request.EmployeeId == null)
        {
            return Results.BadRequest(new ProblemDetails
            {
                Type = "urn:internalchat:bad-request",
                Title = "Missing target",
                Detail = "Must specify either conversationId or employeeId."
            });
        }

        var exportId = Guid.NewGuid();

        await eventPublisher.PublishAsync(new InternalChat.Domain.Exports.ExportRequestedEvent(
            ExportId: exportId,
            ConversationId: request.ConversationId,
            EmployeeId: request.EmployeeId,
            From: request.From,
            To: request.To,
            Reason: request.Reason,
            RequestedByEmployeeId: employee.Id
        ), cancellationToken).ConfigureAwait(false);

        await auditLog.RecordAsync(new AuditEntry(
            Action: "export.requested",
            ActorId: employee.Id,
            SubjectType: "export",
            SubjectId: exportId,
            SourceIp: httpContext.Connection.RemoteIpAddress?.ToString(),
            Outcome: AuditOutcome.Success,
            Detail: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["conversationId"] = request.ConversationId?.ToString() ?? "",
                ["employeeId"] = request.EmployeeId?.ToString() ?? "",
                ["reason"] = request.Reason ?? ""
            }
        ), cancellationToken).ConfigureAwait(false);

        await uow.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The downloadUrl will be the pre-signed URL to MinIO object where the worker will upload it.
        // It's not available until the worker uploads it, but we don't have a database to track export status.
        // For simplicity, we just return the job ID and a placeholder or the URL that will eventually work.
        // Wait, the client can just poll the downloadUrl until it stops returning 404.
        // But we don't have an IObjectStore here to generate it, so we'll just return the job ID and let the client assume the URL.
        // Actually, the API doesn't know the exact URL. We will just return the exportId.

        return Results.Accepted($"/admin/exports/{exportId}", new ExportJobResponse(
            Id: exportId,
            Status: "queued",
            DownloadUrl: null,
            ExpiresAt: null
        ));
    }
}

/// <summary>The current retention policy.</summary>
public sealed record RetentionPolicyResponse(int RetentionMonths, DateTimeOffset AppliesFrom, DateTimeOffset NextSweepAt);

/// <summary>A request to update the retention policy.</summary>
public sealed record RetentionPolicyRequest(int RetentionMonths);

/// <summary>A request to export data.</summary>
public sealed record ExportRequest(Guid? ConversationId, Guid? EmployeeId, DateOnly? From, DateOnly? To, string? Reason);

/// <summary>The status of an export job.</summary>
public sealed record ExportJobResponse(Guid Id, string Status, string? DownloadUrl, DateTimeOffset? ExpiresAt);

