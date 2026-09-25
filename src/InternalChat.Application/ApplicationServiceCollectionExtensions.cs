using InternalChat.Application.Abstractions;
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

        // BCL-only, so it lives here rather than in Infrastructure; both hosts record through it
        // (002 FR-011). Singleton because instruments are created once per meter.
        services.TryAddSingleton<IDeliveryMetrics, Telemetry.DeliveryMetrics>();
        services.TryAddScoped<IUseCaseDispatcher, UseCaseDispatcher>();

        // The one authorization decision this platform has (Principle IV). Scoped because the
        // membership reader behind it reads through the request's unit of work on a cache miss.
        services.TryAddScoped<
            Authorization.IConversationMembershipEvaluator,
            Authorization.ConversationMembershipEvaluator>();

        // Sign-in, sign-out, and access denials — the security events that happen outside a use
        // case and so never reach the pipeline behavior below (FR-006, T072).
        services.TryAddScoped<ISecurityAuditor, SecurityAuditor>();

        // Use cases. Registered against IUseCase<,> so the dispatcher can find them and wrap them
        // in the behavior pipeline — a handler resolved directly would skip validation, the
        // transaction, and the audit record.
        services.TryAddScoped<
            IUseCase<Directory.SyncEmployee, Directory.SyncEmployeeResult>,
            Directory.SyncEmployeeHandler>();

        // US2 — conversations and messages (T091–T094, T097).
        services.TryAddScoped<
            IUseCase<Conversations.CreateConversation, Conversations.CreateConversationResult>,
            Conversations.CreateConversationHandler>();
        services.TryAddScoped<
            IUseCase<Conversations.ListConversations, Conversations.ConversationPage>,
            Conversations.ListConversationsHandler>();
        services.TryAddScoped<
            IUseCase<Conversations.GetConversation, ConversationSummary>,
            Conversations.GetConversationHandler>();
        services.TryAddScoped<
            IUseCase<Messages.SendMessage, Messages.SendMessageResult>,
            Messages.SendMessageHandler>();
        services.TryAddScoped<
            IUseCase<Messages.GetHistory, Messages.HistoryPage>,
            Messages.GetHistoryHandler>();
        services.TryAddScoped<
            IUseCase<Messages.EditMessage, Domain.Messages.Message>,
            Messages.EditMessageHandler>();
        services.TryAddScoped<
            IUseCase<Messages.DeleteMessage, Domain.Messages.Message>,
            Messages.DeleteMessageHandler>();
        services.TryAddScoped<
            IUseCase<Messages.ResyncConversations, Messages.ResyncResult>,
            Messages.ResyncConversationsHandler>();

        // US3 — group membership (T114, T115).
        services.TryAddScoped<
            IUseCase<Conversations.AddMember, Domain.Conversations.Membership>,
            Conversations.AddMemberHandler>();
        services.TryAddScoped<
            IUseCase<Conversations.RemoveMember, Domain.Conversations.Membership>,
            Conversations.RemoveMemberHandler>();
        services.TryAddScoped<
            IUseCase<Conversations.ListMembers, IReadOnlyList<MemberProjection>>,
            Conversations.ListMembersHandler>();
        services.TryAddScoped<
            IUseCase<Conversations.MuteConversation, Domain.Conversations.Membership>,
            Conversations.MuteConversationHandler>();

        // US4 — notifications and read state (T131, T132).
        services.TryAddScoped<
            IUseCase<Notifications.MarkRead, Notifications.MarkReadResult>,
            Notifications.MarkReadHandler>();
        services.TryAddScoped<
            IUseCase<Notifications.GetPreferences, Domain.Notifications.NotificationPreference>,
            Notifications.GetPreferencesHandler>();
        services.TryAddScoped<
            IUseCase<Notifications.UpdatePreferences, Domain.Notifications.NotificationPreference>,
            Notifications.UpdatePreferencesHandler>();
        services.TryAddScoped<
            IUseCase<Notifications.RegisterPushSubscription, bool>,
            Notifications.RegisterPushSubscriptionHandler>();

        // Meetings (T187, T188).
        services.TryAddScoped<
            IUseCase<Meetings.StartMeeting, Meetings.StartMeetingResult>,
            Meetings.StartMeetingHandler>();
        services.TryAddScoped<
            IUseCase<Meetings.IssueJoinToken, Meetings.JoinTicket>,
            Meetings.IssueJoinTokenHandler>();
        services.TryAddScoped<
            IUseCase<Meetings.ApplyMediaSignal, bool>,
            Meetings.ApplyMediaSignalHandler>();
        services.TryAddScoped<
            IUseCase<Meetings.StartScreenShare, Meetings.ScreenShareClaim>,
            Meetings.StartScreenShareHandler>();
        services.TryAddScoped<
            IUseCase<Meetings.StopScreenShare, bool>,
            Meetings.StopScreenShareHandler>();

        // Search (T165).
        services.TryAddScoped<
            IUseCase<Search.SearchMessages, Search.SearchResultPage>,
            Search.SearchMessagesHandler>();
        services.TryAddScoped<IValidator<Search.SearchMessages>, Search.SearchMessagesValidator>();

        // Attachments (T151, T153).
        services.TryAddScoped<
            IUseCase<Attachments.RequestUpload, Attachments.UploadTicket>,
            Attachments.RequestUploadHandler>();
        services.TryAddScoped<
            IUseCase<Attachments.AuthorizeDownload, Attachments.AuthorizedContent>,
            Attachments.AuthorizeDownloadHandler>();
        services.TryAddScoped<
            IUseCase<Attachments.GetAttachmentMetadata, Domain.Attachments.Attachment>,
            Attachments.GetAttachmentMetadataHandler>();
        services.TryAddScoped<
            IUseCase<Attachments.OpenAttachmentContent, Stream>,
            Attachments.OpenAttachmentContentHandler>();

        services.TryAddScoped<
            IValidator<Attachments.RequestUpload>,
            Attachments.RequestUploadValidator>();

        services.TryAddScoped<IValidator<Notifications.MarkRead>, Notifications.MarkReadValidator>();

        // 002 FR-011 — browsers reporting client-observed delivery lag.
        services.TryAddScoped<IUseCase<Telemetry.RecordDeliveryLag, bool>, Telemetry.RecordDeliveryLagHandler>();
        services.TryAddScoped<IValidator<Telemetry.RecordDeliveryLag>, Telemetry.RecordDeliveryLagValidator>();
        services.TryAddScoped<
            IValidator<Notifications.UpdatePreferences>,
            Notifications.UpdatePreferencesValidator>();
        services.TryAddScoped<
            IValidator<Notifications.RegisterPushSubscription>,
            Notifications.RegisterPushSubscriptionValidator>();

        // Validators. Registered per request type rather than by assembly scan, so a use case whose
        // validator was never written fails to validate loudly at review rather than silently at
        // runtime — ValidationBehavior finds no validator and lets everything through.
        services.TryAddScoped<IValidator<Messages.SendMessage>, Messages.SendMessageValidator>();
        services.TryAddScoped<IValidator<Messages.EditMessage>, Messages.EditMessageValidator>();
        services.TryAddScoped<IValidator<Messages.GetHistory>, Messages.GetHistoryValidator>();
        services.TryAddScoped<
            IValidator<Conversations.CreateConversation>,
            Conversations.CreateConversationValidator>();

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
