using System.Text.Json;
using System.Text.Json.Serialization;
using D365FO.Compare.Application.Metadata;
using D365FO.Compare.Domain.Metadata;
using D365FO.Compare.Infrastructure.FoHttp;
using Microsoft.Extensions.Configuration;

namespace D365FO.Compare.Infrastructure.RestMetadata;

public sealed record DataEntityTransportDto
{
    public string? Name { get; init; }
    public string? PublicEntityName { get; init; }
    public string? PublicCollectionName { get; init; }
    public string? EntityCategory { get; init; }
    public JsonElement? Fields { get; init; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalProperties { get; init; } = [];
    public CatalogEntry ToCanonical() => new(Name, PublicEntityName, PublicCollectionName, EntityCategory);
}

public sealed class DataEntitiesClient(FoMetadataHttpClient http, IConfiguration? configuration = null)
{
    private readonly int _maxPages = int.TryParse(configuration?["Metadata:MaxCatalogPages"], out var pages) ? Math.Clamp(pages, 1, 10000) : 100;
    private sealed record Envelope(
        [property: JsonPropertyName("value")] List<DataEntityTransportDto>? Value,
        [property: JsonPropertyName("@odata.nextLink")] string? NextLink);

    public async Task<IReadOnlyList<CatalogEntry>> LoadAsync(Uri origin, string token,
        IProgress<MetadataProgress>? progress, CancellationToken cancellationToken)
    {
        var result = new List<CatalogEntry>();
        Uri? next = new(origin, "/Metadata/DataEntities");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (next is not null)
        {
            if (visited.Count >= _maxPages || !visited.Add(next.AbsoluteUri))
                throw new InvalidDataException("DataEntities pagination exceeded its limit or contains a loop.");
            await using var file = await http.DownloadAsync(next, token, "/Metadata/DataEntities", "application/json", progress, cancellationToken).ConfigureAwait(false);
            var page = await JsonSerializer.DeserializeAsync<Envelope>(file, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (page?.Value is null) throw new InvalidDataException("DataEntities response must contain a value array.");
            result.AddRange(page.Value.Select(item => item.ToCanonical()));
            next = page.NextLink is null ? null : ValidateNextLink(origin, next, page.NextLink);
        }
        return result;
    }

    public static Uri ValidateNextLink(Uri origin, Uri current, string link)
    {
        if (!Uri.TryCreate(current, link, out var next) || next.Scheme != Uri.UriSchemeHttps
            || !string.Equals(next.Authority, origin.Authority, StringComparison.OrdinalIgnoreCase)
            || next.UserInfo.Length != 0 || next.Fragment.Length != 0
            || !string.Equals(next.AbsolutePath.TrimEnd('/'), "/Metadata/DataEntities", StringComparison.Ordinal))
            throw new InvalidDataException("DataEntities nextLink must remain on the same HTTPS metadata endpoint.");
        return next;
    }
}