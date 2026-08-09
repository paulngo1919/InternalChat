// Composition root for the API host.
//
// Constitution Principle I: this file is the ONLY place in this project permitted to name an
// Infrastructure type. tests/Architecture/CompositionRootTests.cs fails the build otherwise.
//
// Constitution Principle IV: every endpoint and hub method declares an explicit authorization
// policy. tests/Architecture/AuthorizationCoverageTests.cs fails the build on any omission,
// which is what makes deny-by-default a gate rather than a habit.

using InternalChat.Api.Observability;
using InternalChat.Api.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// RFC 9457 error responses. Registered before anything else so a failure during startup of a
// later component still surfaces as a well-formed problem document rather than a raw stack trace.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<InternalChat.Api.Middleware.ProblemDetailsHandler>();

builder.Services.AddChatRateLimiting();
builder.Services.AddChatObservability(builder.Configuration, serviceName: "internalchat-api");

// Registered in later phases:
//   builder.Services.AddApplication();                              // T024
//   builder.Services.AddInfrastructure(builder.Configuration);      // T025, T032, T035
//   builder.Services.AddChatAuthentication(builder.Configuration);  // T062
//   builder.Services.AddChatAuthorization();                        // T066, T067
//   builder.Services.AddSignalR().AddStackExchangeRedis(...);       // T097

var app = builder.Build();

// Must be first in the pipeline: anything registered before it can throw outside its reach and
// return a default error page, which is where stack traces escape.
app.UseExceptionHandler();

// After the exception handler so a rejection is still shaped as Problem Details, but before
// endpoints so the limit is applied prior to any work being done.
app.UseRateLimiter();

// Liveness and readiness probes are required by the constitution's container rules.
// They are deliberately anonymous — a probe that needs a token cannot report an outage.
app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" })).AllowAnonymous();

await app.RunAsync().ConfigureAwait(false);

/// <summary>
/// Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can host the API in integration and
/// contract tests. Top-level statements otherwise generate an internal entry point.
/// </summary>
public partial class Program;
