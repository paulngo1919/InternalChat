// Composition root for the API host.
//
// Constitution Principle I: this file is the ONLY place in this project permitted to name an
// Infrastructure type. tests/Architecture/CompositionRootTests.cs fails the build otherwise.
//
// Constitution Principle IV: every endpoint and hub method declares an explicit authorization
// policy. tests/Architecture/AuthorizationCoverageTests.cs fails the build on any omission,
// which is what makes deny-by-default a gate rather than a habit.

var builder = WebApplication.CreateBuilder(args);

// Registered in later phases:
//   builder.Services.AddApplication();                              // T024
//   builder.Services.AddInfrastructure(builder.Configuration);      // T025, T032, T035
//   builder.Services.AddChatAuthentication(builder.Configuration);  // T062
//   builder.Services.AddChatAuthorization();                        // T066, T067
//   builder.Services.AddSignalR().AddStackExchangeRedis(...);       // T097
//   builder.Services.AddChatRateLimiting();                         // T043
//   builder.Services.AddChatObservability();                        // T045

var app = builder.Build();

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
