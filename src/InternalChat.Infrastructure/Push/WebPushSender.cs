using System.Net;
using System.Text.Json;
using InternalChat.Application.Abstractions;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InternalChat.Infrastructure.Push;

/// <summary>
/// T134 — sends browser push notifications over VAPID (research.md D10).
/// </summary>
/// <remarks>
/// <para>
/// <c>Lib.Net.Http.WebPush</c> (MIT) does the RFC 8291 payload encryption and RFC 8292 VAPID
/// signing; this class only maps this platform's types onto its API and its exceptions onto
/// <see cref="PushDeliveryResult"/>. It talks directly to whatever endpoint the subscription names
/// — a browser vendor's push service — with no SDK, account, or fee, which is what keeps this
/// self-hosted rather than a hidden dependency on a paid notification service (Principle VIII).
/// </para>
/// <para>
/// <b>404 and 410 are the two codes that mean "gone".</b> RFC 8030 §7.2: a push service returns 404
/// when the subscription URL is not found and 410 when it "is no longer valid and should not be
/// used again" — both are <see cref="PushDeliveryResult.SubscriptionExpired"/>, which the caller
/// must delete rather than retry (data-model.md). Every other failure — a timeout, a 429, a 5xx —
/// is transient: the endpoint may work again on the next message, and deleting it would drop a
/// device that comes back.
/// </para>
/// </remarks>
public sealed partial class WebPushSender : IPushSender, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly PushServiceClient _client;
    private readonly VapidAuthentication _authentication;
    private readonly ILogger<WebPushSender> _logger;

    /// <summary>Creates the sender.</summary>
    public WebPushSender(
        HttpClient httpClient,
        IOptions<VapidOptions> options,
        ILogger<WebPushSender> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        VapidOptions vapid = options.Value;

        if (string.IsNullOrWhiteSpace(vapid.PublicKey)
            || string.IsNullOrWhiteSpace(vapid.PrivateKey)
            || string.IsNullOrWhiteSpace(vapid.Subject))
        {
            // Checked here rather than with IOptions<T>.ValidateOnStart(): only the Worker host
            // ever constructs this class (it alone resolves IPushSender), so failing here fails
            // the host that actually needs these settings without forcing the API — which shares
            // AddInfrastructure() but never sends a push — to also configure them.
            throw new InvalidOperationException(
                $"{VapidOptions.SectionName}:PublicKey, PrivateKey, and Subject must all be "
                + "configured before a push can be sent. Without a subject several browser "
                + "vendors' push services reject every VAPID request.");
        }

        _client = new PushServiceClient(httpClient);
        _authentication = new VapidAuthentication(vapid.PublicKey, vapid.PrivateKey)
        {
            Subject = vapid.Subject,
        };
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PushDeliveryResult> SendAsync(
        PushSubscriptionDescriptor subscription,
        PushPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(payload);

        PushSubscription target = new() { Endpoint = subscription.Endpoint.ToString() };
        target.SetKey(PushEncryptionKeyName.P256DH, subscription.P256dh);
        target.SetKey(PushEncryptionKeyName.Auth, subscription.Auth);

        string content = JsonSerializer.Serialize(
            new { title = payload.Title, body = payload.Body, deepLink = payload.DeepLink },
            SerializerOptions);

        PushMessage message = new(content) { Urgency = PushMessageUrgency.Normal };

        try
        {
            await _client
                .RequestPushMessageDeliveryAsync(target, message, _authentication, cancellationToken)
                .ConfigureAwait(false);

            return PushDeliveryResult.Delivered;
        }
        catch (PushServiceClientException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            SubscriptionGone(_logger, subscription.SubscriptionId, ex.StatusCode);
            return PushDeliveryResult.SubscriptionExpired;
        }
        catch (PushServiceClientException ex)
        {
            DeliveryFailed(_logger, subscription.SubscriptionId, ex.StatusCode, ex);
            return PushDeliveryResult.TransientFailure;
        }
        catch (HttpRequestException ex)
        {
            DeliveryUnreachable(_logger, subscription.SubscriptionId, ex);
            return PushDeliveryResult.TransientFailure;
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // A client-side timeout, distinct from the caller cancelling — that case propagates.
            DeliveryUnreachable(_logger, subscription.SubscriptionId, ex);
            return PushDeliveryResult.TransientFailure;
        }
    }

    [LoggerMessage(
        EventId = 6000,
        Level = LogLevel.Information,
        Message = "Push subscription {SubscriptionId} reported {StatusCode}; it will be removed rather than retried")]
    private static partial void SubscriptionGone(ILogger logger, Guid subscriptionId, HttpStatusCode statusCode);

    [LoggerMessage(
        EventId = 6001,
        Level = LogLevel.Warning,
        Message = "Push delivery to subscription {SubscriptionId} failed with {StatusCode}")]
    private static partial void DeliveryFailed(
        ILogger logger, Guid subscriptionId, HttpStatusCode statusCode, Exception exception);

    [LoggerMessage(
        EventId = 6002,
        Level = LogLevel.Warning,
        Message = "Push delivery to subscription {SubscriptionId} could not reach the push service")]
    private static partial void DeliveryUnreachable(ILogger logger, Guid subscriptionId, Exception exception);

    /// <inheritdoc />
    public void Dispose() => _authentication.Dispose();
}
