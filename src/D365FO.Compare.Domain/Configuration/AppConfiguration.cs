using D365FO.Compare.Domain.Environments;

namespace D365FO.Compare.Domain.Configuration;

public sealed record AuthenticationContext(
    string TenantId,
    string AccountUsername,
    string HomeAccountId);

public sealed record ConnectionProfile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string TenantId { get; init; }
    public string? ClientId { get; init; }
    public AuthenticationContext? AuthenticationContext { get; init; }
    public EnvironmentConnection? SourceEnvironment { get; init; }
    public EnvironmentConnection? TargetEnvironment { get; init; }
}

public sealed record AppConfiguration
{
    public int SchemaVersion { get; init; } = 3;
    public string? ActiveProfileId { get; init; }
    public IReadOnlyList<ConnectionProfile> Profiles { get; init; } = [];
}
