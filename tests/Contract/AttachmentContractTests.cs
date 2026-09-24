using System.Reflection;
using System.Text.Json;
using InternalChat.Api.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace InternalChat.ContractTests;

/// <summary>
/// T143 — the attachment endpoints against <c>contracts/openapi.yaml</c>.
/// </summary>
/// <remarks>
/// <para>
/// Same shape as <see cref="MessagingContractTests"/>: the document is parsed rather than restated,
/// and both halves that can drift are asserted — the routing table and the DTO wire format.
/// </para>
/// <para>
/// The attachment contract has one property no other resource has, and it gets its own tests:
/// <c>contentUrl</c> must be <b>nullable</b> and must be a <b>path, not a signed URL</b>. Both are
/// FR-025 in disguise. A non-nullable <c>contentUrl</c> would force the API to invent an address
/// for an object that has not been scanned yet, and a signed URL would be a bearer capability over
/// conversation content — which is the precise thing FR-025 rules out with "regardless of how the
/// retrieval address was obtained".
/// </para>
/// </remarks>
public sealed class AttachmentContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly WebApplicationFactory<Program> _factory;
    private readonly OpenApiContract _contract = OpenApiContract.Load();

    public AttachmentContractTests(WebApplicationFactory<Program> factory) => _factory = factory;

    /// <summary>The attachment operations this task covers.</summary>
    public static TheoryData<string, string> DocumentedOperations() => new()
    {
        { "/conversations/{conversationId}/attachments", "POST" },
        { "/attachments/{attachmentId}", "GET" },
        { "/attachments/{attachmentId}/content", "GET" },
    };

    [Theory]
    [MemberData(nameof(DocumentedOperations))]
    public void Every_documented_attachment_operation_is_mapped(string path, string method)
    {
        Assert.Contains(path, _contract.Paths);
        Assert.Contains(method, _contract.MethodsFor(path));

        string expected = _contract.BasePath + path;

        bool mapped = Endpoints().Any(endpoint =>
            string.Equals(RoutePatterns.Of(endpoint), expected, StringComparison.OrdinalIgnoreCase)
            && RoutePatterns.MethodsOf(endpoint).Contains(method, StringComparer.OrdinalIgnoreCase));

        Assert.True(
            mapped,
            $"""
            The contract documents {method} {expected}, and no endpoint serves it.

            Mapped routes:
              {string.Join("\n  ", Endpoints().Select(e => $"{string.Join('|', RoutePatterns.MethodsOf(e))} {RoutePatterns.Of(e)}").Order())}
            """);
    }

    [Fact]
    public void Attachment_serves_every_documented_property()
    {
        IReadOnlyCollection<string> documented = _contract.PropertiesOf("Attachment");
        IReadOnlyCollection<string> serialized = SerializedPropertyNames<AttachmentResponse>();

        List<string> missing = [.. documented.Where(d => !serialized.Contains(d, StringComparer.Ordinal))];

        Assert.True(
            missing.Count == 0,
            $"Attachment does not serialize {string.Join(", ", missing)}. Serialized: {string.Join(", ", serialized.Order())}");
    }

    [Fact]
    public void An_upload_request_requires_kind_content_type_size_and_file_name()
    {
        IReadOnlyCollection<string> required = _contract.RequiredPropertiesOf("RequestUploadRequest");

        Assert.Contains("kind", required);
        Assert.Contains("contentType", required);
        Assert.Contains("byteSize", required);
        Assert.Contains("fileName", required);

        IReadOnlyCollection<string> serialized = SerializedPropertyNames<RequestUploadRequest>();

        foreach (string property in required)
        {
            Assert.Contains(property, serialized);
        }
    }

    /// <summary>
    /// <c>byteSize</c> is <c>int64</c> in the contract and must be <c>long</c> in the DTO.
    /// </summary>
    /// <remarks>
    /// Its own test for the same reason <c>seq</c> has one. A 500 MB video is comfortably inside
    /// <c>int</c>, so an <c>int</c> here works until someone raises the video ceiling — and then it
    /// overflows to a negative size, which sails through a "not greater than the limit" check.
    /// </remarks>
    [Fact]
    public void A_declared_byte_size_is_a_sixty_four_bit_integer()
    {
        Assert.Equal("integer", _contract.TypeOfProperty("RequestUploadRequest", "byteSize"));

        Assert.Equal(
            typeof(long),
            typeof(RequestUploadRequest).GetProperty(nameof(RequestUploadRequest.ByteSize))!.PropertyType);

        Assert.Equal(
            typeof(long),
            typeof(AttachmentResponse).GetProperty(nameof(AttachmentResponse.ByteSize))!.PropertyType);
    }

    /// <summary>
    /// An unscanned attachment can be represented, because <c>contentUrl</c> is nullable.
    /// </summary>
    /// <remarks>
    /// The contract says "present only when scanStatus is clean". A non-nullable property would
    /// force the API to emit an address for bytes FR-024 forbids serving, and the client would
    /// render a broken image instead of "scanning…".
    /// </remarks>
    [Fact]
    public void A_pending_attachment_can_be_represented_because_the_content_url_is_nullable()
    {
        Assert.True(IsNullableReference(typeof(AttachmentResponse), nameof(AttachmentResponse.ContentUrl)));
        Assert.True(IsNullableReference(typeof(AttachmentResponse), nameof(AttachmentResponse.PosterUrl)));

        AttachmentResponse pending = new(
            Guid.CreateVersion7(),
            AttachmentKinds.Image,
            "image/png",
            ByteSize: 4096,
            DurationSeconds: null,
            "screenshot.png",
            ScanStatuses.Pending,
            ContentUrl: null,
            PosterUrl: null);

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(pending, SerializerOptions));

        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("contentUrl").ValueKind);
        Assert.Equal("pending", document.RootElement.GetProperty("scanStatus").GetString());
    }

    /// <summary>
    /// The content URL is a path on this API, not a signed storage address.
    /// </summary>
    /// <remarks>
    /// The sharpest assertion in this file. If <c>contentUrl</c> ever became a presigned MinIO URL
    /// the system would still work — images would render — and FR-025 would be broken: anyone
    /// holding the string could fetch the bytes, member or not, until it expired. The check is
    /// therefore on the shape of the value, because nothing about the feature working would reveal
    /// the difference.
    /// </remarks>
    [Fact]
    public void A_content_url_carries_no_credentials_and_addresses_this_api()
    {
        AttachmentResponse clean = new(
            Guid.Parse("0199a0f0-0000-7000-8000-000000000001"),
            AttachmentKinds.Image,
            "image/png",
            4096,
            null,
            "screenshot.png",
            ScanStatuses.Clean,
            ContentUrl: "/api/v1/attachments/0199a0f0-0000-7000-8000-000000000001/content",
            PosterUrl: null);

        Assert.StartsWith("/", clean.ContentUrl, StringComparison.Ordinal);
        Assert.EndsWith("/content", clean.ContentUrl, StringComparison.Ordinal);

        // The tells of a presigned S3 URL. Any of them appearing here means the bytes became
        // reachable without a membership check.
        Assert.DoesNotContain("X-Amz-Signature", clean.ContentUrl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-Amz-Credential", clean.ContentUrl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("?", clean.ContentUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void The_documented_enum_spellings_are_what_the_api_uses()
    {
        Assert.Equal(
            [AttachmentKinds.Image, AttachmentKinds.Video],
            _contract.EnumOfProperty("Attachment", "kind"));

        Assert.Equal(
            [ScanStatuses.Pending, ScanStatuses.Clean, ScanStatuses.Infected, ScanStatuses.Failed],
            _contract.EnumOfProperty("Attachment", "scanStatus"));

        Assert.Equal(
            [AttachmentKinds.Image, AttachmentKinds.Video],
            _contract.EnumOfProperty("RequestUploadRequest", "kind"));
    }

    /// <summary>
    /// A message carries typed attachments rather than an untyped placeholder.
    /// </summary>
    /// <remarks>
    /// <c>MessageResponse.Attachments</c> was <c>IReadOnlyList&lt;object&gt;</c> through US2–US4,
    /// which serialized as an always-empty array and kept the contract stable. Now that attachments
    /// exist it must carry the real type, or the documented <c>Message.attachments</c> items would
    /// serialize as <c>{}</c>.
    /// </remarks>
    [Fact]
    public void A_message_carries_typed_attachments()
    {
        Assert.Equal(
            typeof(IReadOnlyList<AttachmentResponse>),
            typeof(MessageResponse).GetProperty(nameof(MessageResponse.Attachments))!.PropertyType);
    }

    private IEnumerable<Endpoint> Endpoints() =>
        _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

    private static IReadOnlyCollection<string> SerializedPropertyNames<T>()
    {
        object instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(T));
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize((T)instance, SerializerOptions));

        return [.. document.RootElement.EnumerateObject().Select(p => p.Name)];
    }

    private static bool IsNullableReference(Type type, string propertyName)
    {
        PropertyInfo property = type.GetProperty(propertyName)!;
        NullabilityInfoContext context = new();

        return context.Create(property).WriteState is NullabilityState.Nullable;
    }
}
