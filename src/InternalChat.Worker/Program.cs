// Composition root for the Worker host.
//
// Constitution Principle I: this file is the ONLY place in this project permitted to name an
// Infrastructure type. tests/Architecture/CompositionRootTests.cs fails the build otherwise.
//
// Constitution Principle V: this host runs at a single replica. Scheduled jobs and queue
// consumers live here rather than inside the API so they execute exactly once regardless of
// how many API instances are running.

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddHttpClient();

// Registered in later phases:
//   builder.Services.AddApplication();
//   builder.Services.AddInfrastructure(builder.Configuration);
//   builder.Services.AddHostedService<OutboxDispatcher>();          // T037
//   builder.Services.AddHostedService<PartitionMaintenanceJob>();   // T089
//   builder.Services.AddHostedService<RetentionSweepJob>();         // T206

var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
