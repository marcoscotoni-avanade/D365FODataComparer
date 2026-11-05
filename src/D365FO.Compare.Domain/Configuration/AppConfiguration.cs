using D365FO.Compare.Domain.Environments;

namespace D365FO.Compare.Domain.Configuration;

public sealed record AuthenticationContext(
    string TenantId,
    string AccountUsername,
    string HomeAccountId);

public sealed record AppConfiguration
{
    public int SchemaVersion { get; init; } = 1;
    public AuthenticationContext? AuthenticationContext { get; init; }
    public EnvironmentConnection? SourceEnvironment { get; init; }
    public EnvironmentConnection? TargetEnvironment { get; init; }
}