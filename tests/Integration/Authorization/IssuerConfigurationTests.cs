using System.Net;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;

namespace InternalChat.IntegrationTests.Authorization;

/// <summary>
/// <c>Keycloak:Issuer</c> — the issuer the API expects when the browser reaches Keycloak at a
/// different address from the one the API uses (a public hostname behind a tunnel).
/// </summary>
/// <remarks>
/// <para>
/// Reproduced with the one lever a test has over the issuer: Keycloak stamps the address a token was
/// requested through, so a token requested via <c>127.0.0.1</c> carries a different <c>iss</c> from
/// one requested via <c>localhost</c> — exactly the split a browser on
/// <c>https://chat.example</c> and an API on <c>http://keycloak:8080</c> produce.
/// </para>
/// <para>
/// The JWT handler always also accepts the issuer in the metadata it fetched from the authority, so
/// the default configuration keeps working; what this setting adds is the second, public issuer.
/// </para>
/// </remarks>
public sealed class IssuerConfigurationTests : IntegrationTestBase
{
    public IssuerConfigurationTests(StackFixture stack)
        : base(stack)
    {
    }

    [Fact]
    public async Task A_token_issued_under_another_address_is_refused_by_default()
    {
        HttpStatusCode status = await MeAsync(configuredIssuer: null);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task A_token_issued_under_another_address_is_accepted_when_that_issuer_is_configured()
    {
        HttpStatusCode status = await MeAsync(configuredIssuer: AlternateAuthority());

        Assert.Equal(HttpStatusCode.OK, status);
    }

    /// <summary>The realm URL spelled with the other loopback name.</summary>
    private string AlternateAuthority()
    {
        string authority = Stack.RealmAuthority;

        return authority.Contains("://localhost", StringComparison.Ordinal)
            ? authority.Replace("://localhost", "://127.0.0.1", StringComparison.Ordinal)
            : authority.Replace("://127.0.0.1", "://localhost", StringComparison.Ordinal);
    }

    private async Task<HttpStatusCode> MeAsync(string? configuredIssuer)
    {
        await using (ChatDbContext context = CreateDbContext())
        {
            await TestData.SeedEmployeeAsync(context, "an.nguyen");
        }

        Dictionary<string, string?> overrides = new(StringComparer.Ordinal);
        if (configuredIssuer is not null)
        {
            overrides["Keycloak:Issuer"] = configuredIssuer;
        }

        await using ApiFactory api = new(Stack, overrides);

        using HttpClient client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await StackFixture.IssueAccessTokenViaAsync("an.nguyen", AlternateAuthority()));

        using HttpResponseMessage response = await client.GetAsync(new Uri("/api/v1/me", UriKind.Relative));

        return response.StatusCode;
    }
}
