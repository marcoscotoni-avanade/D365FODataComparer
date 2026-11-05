namespace D365FO.Compare.Domain.Environments;

public sealed record EnvironmentConnection
{
    public required string ProviderEnvironmentId { get; init; }
    public required string DisplayName { get; init; }
    public required string TenantId { get; init; }
    public required Uri FinOpsUrl { get; init; }
    public string? FinOpsEnvironmentId { get; init; }
    public string? FinOpsEnvironmentType { get; init; }
    public string? PowerPlatformType { get; init; }
    public string? State { get; init; }
    public string? Region { get; init; }
    public ConnectionValidationStatus ValidationStatus { get; init; }
    public DateTimeOffset? LastValidatedUtc { get; init; }

    public static EnvironmentConnection FromDiscovered(DiscoveredEnvironment environment) => new()
    {
        ProviderEnvironmentId = environment.ProviderEnvironmentId,
        DisplayName = environment.DisplayName,
        TenantId = environment.TenantId,
        FinOpsUrl = environment.FinOpsUrl ?? throw new ArgumentException("The environment has no FinOps URL.", nameof(environment)),
        FinOpsEnvironmentId = environment.FinOpsEnvironmentId,
        FinOpsEnvironmentType = environment.FinOpsEnvironmentType,
        PowerPlatformType = environment.PowerPlatformType,
        State = environment.State,
        Region = environment.Region,
        ValidationStatus = ConnectionValidationStatus.NotTested
    };
}

public enum ConnectionValidationStatus
{
    NotTested,
    Succeeded,
    Failed,
    RequiresRevalidation
}

public sealed record ConnectionValidationResult(
    ConnectionValidationStatus Status,
    string Message,
    DateTimeOffset ValidatedUtc)
{
    public bool IsSuccessful => Status == ConnectionValidationStatus.Succeeded;
}