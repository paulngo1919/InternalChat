using System.Reflection;

namespace InternalChat.TestSupport;

/// <summary>
/// Locates repository files that tests read as fixtures — the OpenAPI contract, the Keycloak realm.
/// </summary>
/// <remarks>
/// <para>
/// A test binary runs from <c>bin/Debug/net10.0</c>, so every one of these paths would otherwise be
/// a hand-counted <c>../../../../..</c> that silently breaks the day a project moves one directory.
/// Walking up to a known root marker instead means the answer is derived, not asserted.
/// </para>
/// <para>
/// Linked into each test project rather than published as a package: it is twenty lines, and
/// Principle VIII prefers one fewer artefact to version.
/// </para>
/// </remarks>
public static class RepositoryPaths
{
    /// <summary>The file that marks the repository root. Present exactly once.</summary>
    private const string RootMarker = "InternalChat.slnx";

    private static readonly Lazy<string> LazyRoot = new(FindRoot);

    /// <summary>Absolute path to the repository root.</summary>
    public static string Root => LazyRoot.Value;

    /// <summary>The feature's specification directory.</summary>
    public static string FeatureDirectory =>
        Path.Combine(Root, "specs", "001-enterprise-chat-platform");

    /// <summary>The HTTP contract every endpoint is tested against (Principle VI).</summary>
    public static string OpenApiContract =>
        Path.Combine(FeatureDirectory, "contracts", "openapi.yaml");

    /// <summary>
    /// The development Keycloak realm (T061).
    /// </summary>
    /// <remarks>
    /// Integration tests import this exact file into their Keycloak container rather than
    /// configuring a realm of their own. A test realm that drifted from the deployed one would
    /// prove the authentication path works against a realm nobody runs.
    /// </remarks>
    public static string KeycloakRealmExport =>
        Path.Combine(Root, "deploy", "keycloak", "realm-export.json");

    private static string FindRoot()
    {
        // Assembly location rather than the current directory: a test runner is free to set the
        // working directory anywhere, and some do.
        string start = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
            ?? AppContext.BaseDirectory;

        for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, RootMarker)))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Could not find '{RootMarker}' in any ancestor of '{start}'. Tests read repository "
            + "files (the OpenAPI contract, the Keycloak realm) as fixtures and cannot run without "
            + "knowing where the repository root is.");
    }
}
