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
// How authorized attachment bytes reach the client: X-Accel-Redirect in every deployment, direct
// streaming where no reverse proxy is in front (research.md D7, T153).
builder.Services.Configure<InternalChat.Api.Endpoints.AttachmentDeliveryOptions>(
    builder.Configuration.GetSection(InternalChat.Api.Endpoints.AttachmentDeliveryOptions.SectionName));

// The LiveKit signing secret, bound in the Api project rather than reusing Infrastructure's
// options type — Principle I forbids naming one outside this file (T189).
builder.Services.Configure<InternalChat.Api.Endpoints.MeetingWebhookOptions>(
    builder.Configuration.GetSection(InternalChat.Api.Endpoints.MeetingWebhookOptions.SectionName));

builder.Services.AddScoped<CurrentEmployee>();
builder.Services.AddScoped<AccessGateMiddleware>();
builder.Services.AddSingleton<LogoutTokenValidator>();

// Open hub connections and the timer that re-checks them. Singletons: the registry holds one
// process's sockets, and only the process holding a socket can close it (FR-003, SC-018).
builder.Services.AddSingleton<HubConnectionRegistry>();
builder.Services.AddSingleton<HubAuthorizationFilter>();
builder.Services.AddHostedService<RevocationSweepService>();

// Real-time delivery (T098). Hosted in the API rather than the Worker because only a process
// holding a WebSocket can write to it — the Redis backplane spreads a message across replicas, but
// the event still has to arrive at an API process to be sent. Registered twice for the same reason
// as the Worker's consumers: once as the concrete type so the consumer host can resolve it from the
// per-message scope, and once against the port so the queue service knows which queues to consume.
builder.Services.AddScoped<InternalChat.Api.Consumers.RealtimeFanoutConsumer>();
builder.Services.AddScoped<InternalChat.Application.Abstractions.IMessageConsumer>(
    sp => sp.GetRequiredService<InternalChat.Api.Consumers.RealtimeFanoutConsumer>());

// T116 — moves a connection's SignalR group assignment immediately on a membership change.
// Same double registration as above, and for the same reason.
builder.Services.AddScoped<InternalChat.Api.Consumers.MembershipChangeConsumer>();
builder.Services.AddScoped<InternalChat.Application.Abstractions.IMessageConsumer>(
    sp => sp.GetRequiredService<InternalChat.Api.Consumers.MembershipChangeConsumer>());

// T137 — broadcasts a read-position advance to the same employee's other devices.
builder.Services.AddScoped<InternalChat.Api.Consumers.ReadStateConsumer>();
builder.Services.AddScoped<InternalChat.Application.Abstractions.IMessageConsumer>(
    sp => sp.GetRequiredService<InternalChat.Api.Consumers.ReadStateConsumer>());

// T191 — announces meeting start and end to the conversation's SignalR group. In the API for the
// same reason as the other fan-outs: only a process holding a WebSocket can write to it.
builder.Services.AddScoped<InternalChat.Api.Consumers.MeetingLifecycleConsumer>();
builder.Services.AddScoped<InternalChat.Application.Abstractions.IMessageConsumer>(
    sp => sp.GetRequiredService<InternalChat.Api.Consumers.MeetingLifecycleConsumer>());

builder.Services.AddHostedService<InternalChat.Infrastructure.Messaging.QueueConsumerService>();

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
api.MapConversationEndpoints();
api.MapMembershipEndpoints();
api.MapMessageEndpoints();
api.MapNotificationEndpoints();
api.MapAttachmentEndpoints();
api.MapSearchEndpoints();
api.MapMeetingEndpoints();

// [Authorize] on the hub covers the connect. Re-validation on every invocation is
// HubAuthorizationFilter, and closing an idle connection whose access has ended is
// RevocationSweepService — see contracts/signalr-hub.md and FR-003.
// Outside the /api/v1 group: LiveKit posts to a fixed path that is not part of the client
// contract and is authenticated by signature rather than by a user token (T189).
app.MapMeetingWebhookEndpoints();

app.MapHub<ChatHub>(ChatHub.Path);

await app.RunAsync().ConfigureAwait(false);

/// <summary>
/// Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can host the API in integration and
/// contract tests. Top-level statements otherwise generate an internal entry point.
/// </summary>
public partial class Program;
