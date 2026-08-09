using System.Globalization;
using InternalChat.Seeder;

// Development and load-test data generator.
//
// Two jobs, and they are genuinely different:
//
//   --dev        A small, coherent dataset so `docker compose up` lands on a
//                platform you can sign into and use immediately. The constitution
//                requires onboarding to be three steps; a developer configuring
//                their own test data is a fourth.
//
//   --load N     Bulk message volume for performance verification. research.md D9
//                makes this load-bearing: PostgreSQL full-text search is the v1
//                choice ON CONDITION that it is measured against a full-retention
//                corpus (~125 million messages). A search budget verified on an
//                empty database is not verified, and the decision to avoid
//                OpenSearch rests on that measurement.
//
// Usage:
//   dotnet run --project tools/Seeder -- --dev
//   dotnet run --project tools/Seeder -- --load 125000000
//   dotnet run --project tools/Seeder -- --load 1000000 --connection "Host=...;"

string connectionString =
    GetOption(args, "--connection")
    ?? Environment.GetEnvironmentVariable("INTERNALCHAT_DB_CONNECTION")
    ?? "Host=localhost;Port=5432;Database=internalchat;Username=internalchat;Password=internalchat";

if (args.Contains("--help") || args.Length == 0)
{
    Console.WriteLine(
        """
        InternalChat seeder

          --dev                 Seed a small, usable development dataset.
          --load <count>        Seed <count> messages for performance verification.
          --connection <string> PostgreSQL connection string.
                                Defaults to INTERNALCHAT_DB_CONNECTION, then localhost.
          --help                Show this.

        The load corpus exists because research.md D9 requires search to be measured
        against full retention volume before PostgreSQL FTS is accepted for v1.
        """);
    return 0;
}

try
{
    if (args.Contains("--dev"))
    {
        await DevelopmentSeeder.RunAsync(connectionString).ConfigureAwait(false);
    }

    string? loadArgument = GetOption(args, "--load");
    if (loadArgument is not null)
    {
        if (!long.TryParse(loadArgument, CultureInfo.InvariantCulture, out long count) || count <= 0)
        {
            Console.Error.WriteLine($"--load expects a positive number, got '{loadArgument}'.");
            return 2;
        }

        await LoadSeeder.RunAsync(connectionString, count).ConfigureAwait(false);
    }

    return 0;
}
catch (Exception ex)
{
    // Seeding failures are almost always a connection string or a missing migration.
    // Say which, rather than printing a stack trace nobody reads.
    Console.Error.WriteLine($"Seeding failed: {ex.Message}");
    Console.Error.WriteLine("Check the connection string and that migrations have been applied.");
    return 1;
}

static string? GetOption(string[] args, string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
