namespace D365FO.Compare.Domain.Configuration;

public sealed record GeneralPreferences(int MaxConcurrentCountRequests = GeneralPreferences.DefaultMaxConcurrentCountRequests)
{
    public IReadOnlyList<string> ExcludedEntitySets { get; init; } = [];
    public const int DefaultMaxConcurrentCountRequests = 2;
    public const int MinConcurrentCountRequests = 1;
    public const int MaxConcurrentCountRequestsLimit = 16;

    public bool IsValid => MaxConcurrentCountRequests is >= MinConcurrentCountRequests and <= MaxConcurrentCountRequestsLimit
        && ExcludedEntitySets is not null
        && ExcludedEntitySets.All(name => !string.IsNullOrWhiteSpace(name) && name == name.Trim())
        && ExcludedEntitySets.Distinct(StringComparer.OrdinalIgnoreCase).Count() == ExcludedEntitySets.Count;
    public static GeneralPreferences Default { get; } = new();
}