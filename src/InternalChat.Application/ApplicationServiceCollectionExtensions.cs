using InternalChat.Application.Behaviors;
using InternalChat.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InternalChat.Application;

/// <summary>
/// Registers the Application layer.
/// </summary>
/// <remarks>
/// Registers abstractions and use cases only. Every outbound interface in
/// <c>Application.Abstractions</c> is implemented by Infrastructure and wired at the composition
/// root — Constitution Principle I means this file never names an Infrastructure type, and
/// <c>tests/Architecture/DependencyRuleTests.cs</c> fails the build if it does.
/// </remarks>
public static class ApplicationServiceCollectionExtensions
{
    /// <summary>Adds Application services, the use case dispatcher, and the behavior pipeline.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddScoped<IUseCaseDispatcher, UseCaseDispatcher>();

        // ORDER IS LOAD-BEARING — outermost first. See UseCaseDispatcher for why each position
        // matters. Reordering these changes transactional and audit semantics, not just style.
        services.TryAddEnumerable(
        [
            ServiceDescriptor.Scoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>)),
            ServiceDescriptor.Scoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>)),
            ServiceDescriptor.Scoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>)),
            ServiceDescriptor.Scoped(typeof(IPipelineBehavior<,>), typeof(AuditBehavior<,>)),
        ]);

        return services;
    }
}
