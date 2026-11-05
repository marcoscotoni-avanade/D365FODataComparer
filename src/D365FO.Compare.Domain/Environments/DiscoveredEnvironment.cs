namespace D365FO.Compare.Domain.Environments;

public sealed record DiscoveredEnvironment
{
    public required string ProviderEnvironmentId { get; init; }
    public required string TenantId { get; init; }
    public required string DisplayName { get; init; }
    public string? PowerPlatformType { get; init; }
    public string? State { get; init; }
    public string? Region { get; init; }
    public Uri? DataverseUrl { get; init; }
    public string? FinOpsEnvironmentId { get; init; }
    public string? FinOpsEnvironmentType { get; init; }
    public Uri? FinOpsUrl { get; init; }

    public bool IsSelectableFinOps =>
        FinOpsUrl is { IsAbsoluteUri: true, Scheme: "https", UserInfo.Length: 0, Query.Length: 0, Fragment.Length: 0, AbsolutePath: "/" };
}