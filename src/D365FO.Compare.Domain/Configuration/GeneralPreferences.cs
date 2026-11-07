namespace D365FO.Compare.Domain.Configuration;

public sealed record GeneralPreferences(int MaxConcurrentCountRequests = GeneralPreferences.DefaultMaxConcurrentCountRequests)
{
    public const int DefaultMaxConcurrentCountRequests = 2;
    public const int MinConcurrentCountRequests = 1;
    public const int MaxConcurrentCountRequestsLimit = 16;

    public bool IsValid => MaxConcurrentCountRequests is >= MinConcurrentCountRequests and <= MaxConcurrentCountRequestsLimit;
    public static GeneralPreferences Default { get; } = new();
}