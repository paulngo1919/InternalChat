using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace InternalChat.Infrastructure.Persistence;

/// <summary>
/// Counts database round trips within a scope, so an N+1 pattern fails a test instead of
/// quietly costing latency in production.
/// </summary>
/// <remarks>
/// <para>
/// Constitution Principle V: "N+1 query patterns are a build-blocking defect." A defect can only
/// block a build if something detects it, and N+1 is invisible in a unit test and nearly
/// invisible in a small-dataset integration test — it shows up as a slow endpoint months later,
/// once a conversation has real membership.
/// </para>
/// <para>
/// Scoped rather than global because a budget is only meaningful per operation: fanning out to
/// 500 members legitimately touches the database more than loading one history page.
/// </para>
/// </remarks>
public sealed class QueryCountInterceptor : DbCommandInterceptor
{
    private static readonly AsyncLocal<QueryCountScope?> CurrentScope = new();

    /// <summary>
    /// Starts counting for the current async flow. Dispose to stop and assert.
    /// </summary>
    /// <param name="budget">Maximum permitted round trips before the scope throws on dispose.</param>
    /// <param name="description">What is being measured, used in the failure message.</param>
    public static QueryCountScope BeginScope(int budget, string description)
    {
        QueryCountScope scope = new(budget, description, () => CurrentScope.Value = null);
        CurrentScope.Value = scope;
        return scope;
    }

    /// <summary>Round trips recorded by the active scope, or zero when none is active.</summary>
    public static int CurrentCount => CurrentScope.Value?.Count ?? 0;

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        CurrentScope.Value?.Record(command?.CommandText);
        return base.ReaderExecuting(command!, eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        CurrentScope.Value?.Record(command?.CommandText);
        return base.ReaderExecutingAsync(command!, eventData, result, cancellationToken);
    }

    /// <inheritdoc />
    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        CurrentScope.Value?.Record(command?.CommandText);
        return base.ScalarExecuting(command!, eventData, result);
    }

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        CurrentScope.Value?.Record(command?.CommandText);
        return base.NonQueryExecuting(command!, eventData, result);
    }
}

/// <summary>
/// An active query-counting scope. Throws on dispose when the budget was exceeded.
/// </summary>
public sealed class QueryCountScope : IDisposable
{
    private readonly List<string> _commands = [];
    private readonly Action _onDispose;
    private bool _disposed;

    internal QueryCountScope(int budget, string description, Action onDispose)
    {
        Budget = budget;
        Description = description;
        _onDispose = onDispose;
    }

    /// <summary>Maximum permitted round trips.</summary>
    public int Budget { get; }

    /// <summary>What is being measured.</summary>
    public string Description { get; }

    /// <summary>Round trips recorded so far.</summary>
    public int Count => _commands.Count;

    /// <summary>The SQL executed, in order, for the failure message.</summary>
    public IReadOnlyList<string> Commands => _commands;

    internal void Record(string? commandText) =>
        _commands.Add(commandText ?? "(unknown command)");

    /// <summary>Ends the scope and throws when the budget was exceeded.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _onDispose();

        if (Count <= Budget)
        {
            return;
        }

        // Listing the SQL matters: "17 queries, budget 3" tells you there is a problem, while
        // seeing the same SELECT seventeen times with a different id tells you where it is.
        string executed = string.Join(
            Environment.NewLine,
            _commands.Select((sql, i) => $"  [{i + 1}] {Summarise(sql)}"));

        throw new QueryBudgetExceededException(
            $"""
            {Description} issued {Count} database round trips, budget {Budget}.

            Constitution Principle V treats N+1 as a build-blocking defect. Repeated near-identical
            statements below usually mean a navigation is being lazily loaded inside a loop — load
            it in one query instead.

            {executed}
            """);
    }

    private static string Summarise(string sql)
    {
        string collapsed = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= 200 ? collapsed : collapsed[..200] + "...";
    }
}

/// <summary>Thrown when a scope exceeds its query budget.</summary>
public sealed class QueryBudgetExceededException : Exception
{
    /// <summary>Creates the exception.</summary>
    public QueryBudgetExceededException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public QueryBudgetExceededException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public QueryBudgetExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
