using InternalChat.Api.Authorization;
using InternalChat.Api.Middleware;
using InternalChat.Application;
using InternalChat.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace InternalChat.IntegrationTests.Fixtures;

/// <summary>
/// Hosts a conversation-scoped endpoint so the authorization pipeline can be tested before US2
/// gives it one of its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The refusal-opacity property (SC-017) belongs to two production
/// components — <see cref="MembershipEndpointFilter"/>, which throws
/// <see cref="UnauthorizedAccessException"/> on refusal, and
/// <see cref="ProblemDetailsHandler"/>, which maps that to a body shaped exactly like a
/// not-found. Both exist. What does not yet exist is an endpoint carrying
/// <c>{conversationId}</c>: <c>GET /conversations/{id}</c> arrives with US2 (T095), and the
/// Phase 3 checkpoint requires the opacity test to pass before that work starts.
/// </para>
/// <para>
/// <b>What it does and does not prove.</b> The filter, the handler, the evaluator, the membership
/// cache, and the access gate are the real ones, resolved from the same registrations
/// <c>Program.cs</c> uses, in the same middleware order — that order is copied deliberately,
/// because putting the exception handler anywhere but first is how a stack trace escapes. What it
/// cannot prove is that <c>Program.cs</c> keeps that order. The other tests in this suite run
/// against the real host and would notice.
/// </para>
/// <para>
/// <b>T095 should retire this.</b> Once the real conversation endpoint is mapped, RefusalOpacity
/// tests should point at it and this file should be deleted rather than left as a second, quietly
/// diverging composition root.
/// </para>
/// </remarks>
public sealed class ProtectedProbeFactory : IAsyncDisposable
{
    /// <summary>Route shaped like the conversation endpoint US2 will add.</summary>
    public const string ConversationRoute = "/api/v1/conversations/{conversationId}";

    private readonly WebApplication _app;

    private ProtectedProbeFactory(WebApplication app) => _app = app;

    /// <summary>An HTTP client bound to the probe host.</summary>
    public HttpClient CreateClient() => _app.GetTestClient();

    /// <summary>Starts the probe host against the containerised stack.</summary>
    public static async Task<ProtectedProbeFactory> StartAsync(StackFixture stack)
    {
        ArgumentNullException.ThrowIfNull(stack);

        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:Postgres"] = stack.PostgresConnectionString,
            ["ConnectionStrings:Redis"] = stack.RedisConnectionString,
            ["Redis:Environment"] = "test",
            ["Keycloak:Authority"] = stack.RealmAuthority,
            ["Keycloak:Audience"] = StackFixture.Audience,
            ["Keycloak:RequireHttpsMetadata"] = "false",
        });

        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<ProblemDetailsHandler>();

        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddChatAuthentication(builder.Configuration);
        builder.Services.AddChatAuthorization();
        builder.Services.AddScoped<CurrentEmployee>();
        builder.Services.AddScoped<AccessGateMiddleware>();

        WebApplication app = builder.Build();

        // Order copied from Program.cs. See the class remarks.
        app.UseExceptionHandler();
        app.UseAuthentication();
        app.UseMiddleware<AccessGateMiddleware>();
        app.UseAuthorization();

        app.MapGet(ConversationRoute, (Guid conversationId) => Results.Ok(new { id = conversationId }))
            .RequireAuthorization(AuthorizationPolicies.ConversationMember)
            .RequireConversationMembership();

        await app.StartAsync();

        return new ProtectedProbeFactory(app);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
