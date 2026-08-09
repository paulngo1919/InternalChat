// Composition root for the API host.
//
// Constitution Principle I: this file is the ONLY place in this project permitted to name an
// Infrastructure type. tests/Architecture/CompositionRootTests.cs fails the build otherwise.
//
// Constitution Principle IV: every endpoint and hub method declares an explicit authorization
// policy. tests/Architecture/AuthorizationCoverageTests.cs fails the build on any omission,
// which is what makes deny-by-default a gate rather than a habit.

using InternalChat.Api.Authorization;
using InternalChat.Api.Endpoints;
using InternalChat.Api.Hubs;
using InternalChat.Api.Observability;
using InternalChat.Api.RateLimiting;
using InternalChat.Application;
using InternalChat.Infrastructure;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

// RFC 9457 error responses. Registered before anything else so a failure during startup of a
// later component still surfaces as a well-formed problem document rather than a raw stack trace.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<InternalChat.Api.Middleware.ProblemDetailsHandler>();

builder.Services.AddChatRateLimiting();
builder.Services.AddChatObservability(builder.Configuration, serviceName: "internalchat-api");

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddChatAuthentication(builder.Configuration);
builder.Services.AddChatAuthorization();

// The identity of the current request, resolved once by the access gate and read by endpoints.
builder.Services.AddScoped<CurrentEmployee>();
builder.Services.AddScoped<AccessGateMiddleware>();
builder.Services.AddSingleton<LogoutTokenValidator>();

// Open hub connections and the timer that re-checks them. Singletons: the registry holds one
// process's sockets, and only the process holding a socket can close it (FR-003, SC-018).
builder.Services.AddSingleton<HubConnectionRegistry>();
builder.Services.AddSingleton<HubAuthorizationFilter>();
builder.Services.AddHostedService<RevocationSweepService>();

builder.Services
    .AddSignalR(options => options.AddFilter<HubAuthorizationFilter>())

    // The options callback runs when RedisOptions is first materialised, not now, so the
    // connection string is read after every configuration source is in place. Reading it here
    // eagerly would bake in whatever appsettings.json says and ignore anything supplied later.
    .AddStackExchangeRedis(options =>
    {
        string connectionString = builder.Configuration.GetConnectionString("Redis")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Redis is not configured. It backs the SignalR backplane; "
                + "without it, delivery would work only for clients that happen to be connected "
                + "to the same replica as the sender.");

        options.Configuration = StackExchange.Redis.ConfigurationOptions.Parse(connectionString);
        options.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("signalr");
    });

var app = builder.Build();

// Must be first in the pipeline: anything registered before it can throw outside its reach and
// return a default error page, which is where stack traces escape.
app.UseExceptionHandler();

// After the exception handler so a rejection is still shaped as Problem Details, but before
// endpoints so the limit is applied prior to any work being done.
app.UseRateLimiter();

app.UseAuthentication();

// Between authentication and authorization, deliberately. It needs a populated principal, and
// every endpoint policy must run after the token has been checked against the revocation set —
// otherwise a revoked employee would still pass an endpoint's own authorization.
app.UseMiddleware<AccessGateMiddleware>();

app.UseAuthorization();

// Liveness and readiness probes are required by the constitution's container rules.
// They are deliberately anonymous — a probe that needs a token cannot report an outage.
app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" })).AllowAnonymous();

// Everything the OpenAPI document describes sits under the versioned prefix its `servers` entry
// declares. Mapping the group in one place means a new endpoint file cannot land outside it.
RouteGroupBuilder api = app.MapGroup("/api/v1");
api.MapAuthEndpoints();
api.MapMeEndpoints();
api.MapDirectoryEndpoints();

// [Authorize] on the hub covers the connect. Re-validation on every invocation is
// HubAuthorizationFilter, and closing an idle connection whose access has ended is
// RevocationSweepService — see contracts/signalr-hub.md and FR-003.
app.MapHub<ChatHub>(ChatHub.Path);

await app.RunAsync().ConfigureAwait(false);

/// <summary>
/// Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can host the API in integration and
/// contract tests. Top-level statements otherwise generate an internal entry point.
/// </summary>
public partial class Program;
