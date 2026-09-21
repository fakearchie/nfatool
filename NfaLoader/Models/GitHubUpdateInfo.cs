namespace NfaLoader.Models;

internal sealed record GitHubUpdateInfo(
    string CurrentVersion,
    string LatestVersion,
    string LatestTag,
    bool IsUpdateAvailable,
    string ReleaseUrl,
    string? ArtifactName,
    string? ArtifactUrl,
    long? ArtifactSize,
    string? ArtifactType,
    string? ArtifactSha256,
    IReadOnlyList<string> Changelog,
    DateTimeOffset CheckedAt);
