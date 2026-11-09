using System.Text.Json;
using D365FO.Compare.Application.Authentication;
using D365FO.Compare.Application.DataComparison;
using D365FO.Compare.Application.Metadata;
using D365FO.Compare.Application.Validation;
using D365FO.Compare.Domain.DataComparison;
using D365FO.Compare.Domain.Environments;
using D365FO.Compare.Domain.Metadata;
using D365FO.Compare.Infrastructure.FoHttp;
using D365FO.Compare.Infrastructure.Metadata;
using Microsoft.Extensions.Configuration;

namespace D365FO.Compare.Infrastructure.ODataData;

public sealed class ODataLegalEntityProvider
    : ILegalEntityProvider
{
    private readonly FoMetadataHttpClient _http;
    private readonly IAuthenticationService _authentication;
    private readonly IFinOpsHostPolicy _hosts;
    private readonly MetadataWindowContext _window;

    public ODataLegalEntityProvider(HttpClient http, IAuthenticationService authentication, IFinOpsHostPolicy hosts,
        MetadataWindowContext window)
    {
        _http = new FoMetadataHttpClient(http, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Metadata:MaxPayloadBytes"] = (8L * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture)
        }).Build());
        _authentication = authentication;
        _hosts = hosts;
        _window = window;
    }

    public async Task<IReadOnlyList<LegalEntityOption>> LoadAsync(EnvironmentConnection environment, SchemaSnapshot schema,
        IProgress<DataReadProgress>? progress, string side, CancellationToken cancellationToken)
    {
        var entity = schema.Entities.SingleOrDefault(entity => entity.EntitySetName == "LegalEntities")
            ?? throw new InvalidDataException("LegalEntities is not exposed in this environment's schema.");
        if (!entity.Fields.Any(property => property.Name == "LegalEntityId" && property.TypeFullName == "Edm.String" && !property.IsCollection))
            throw new InvalidDataException("LegalEntities must expose LegalEntityId as a scalar string.");
        var hasName = entity.Fields.Any(property => property.Name == "Name" && property.TypeFullName == "Edm.String" && !property.IsCollection);
        await ValidateAsync(environment, schema, cancellationToken).ConfigureAwait(false);
        var origin = new Uri(environment.FinOpsUrl.GetLeftPart(UriPartial.Authority));
        var projection = hasName ? "LegalEntityId,Name" : "LegalEntityId";
        var initial = new Uri(origin, "/data/LegalEntities?$select=" + Uri.EscapeDataString(projection) + "&cross-company=true");
        Uri? next = initial;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var companies = new Dictionary<string, LegalEntityOption>(StringComparer.OrdinalIgnoreCase);
        var totalRows = 0;
        long totalBytes = 0;
        while (next is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (visited.Count >= 100 || !visited.Add(next.AbsoluteUri)) throw new DataLimitException("LegalEntities paging exceeded its limit or contains a loop.");
            await ValidateAsync(environment, schema, cancellationToken).ConfigureAwait(false);
            var token = await _authentication.GetAccessTokenAsync(environment.TenantId, [origin.AbsoluteUri.TrimEnd('/').ToLowerInvariant() + "/.default"],
                _window.ParentWindowProvider(), cancellationToken).ConfigureAwait(false);
            await ValidateAsync(environment, schema, cancellationToken).ConfigureAwait(false);
            progress?.Report(new("LegalEntities", side, "Loading setup companies", totalRows, visited.Count));
            await using var file = await _http.DownloadAsync(next, token, "/data/LegalEntities", "application/json", null, cancellationToken).ConfigureAwait(false);
            totalBytes += file.Length;
            if (totalBytes > 32L * 1024 * 1024) throw new DataLimitException("LegalEntities exceeded the cumulative setup byte limit.");
            using var document = await JsonDocument.ParseAsync(file, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("value", out var rows)
                || rows.ValueKind != JsonValueKind.Array) throw new InvalidDataException("LegalEntities must return an OData value array.");
            foreach (var row in rows.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++totalRows > 10000) throw new DataLimitException("LegalEntities exceeded the setup row limit.");
                if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("LegalEntityId", out var id)
                    || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                    throw new InvalidDataException("LegalEntities returned a missing or invalid company code.");
                string? name = null;
                if (hasName && row.TryGetProperty("Name", out var description) && description.ValueKind != JsonValueKind.Null)
                {
                    if (description.ValueKind != JsonValueKind.String) throw new InvalidDataException("LegalEntities returned an invalid company description.");
                    name = description.GetString();
                }
                var option = new LegalEntityOption(id.GetString()!, name);
                if (companies.TryGetValue(option.Code, out var previous) && previous != option)
                    throw new InvalidDataException("LegalEntities returned conflicting duplicate company codes.");
                companies[option.Code] = option;
            }
            var link = document.RootElement.TryGetProperty("@odata.nextLink", out var nextLink) ? nextLink.GetString() : null;
            next = link is null ? null : ODataEntityDataReader.ValidateNextLink(origin, next, "/data/LegalEntities", link, initial);
            await ValidateAsync(environment, schema, cancellationToken).ConfigureAwait(false);
        }
        return companies.Values.OrderBy(company => company.Code, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task ValidateAsync(EnvironmentConnection environment, SchemaSnapshot schema, CancellationToken cancellationToken)
    {
        if (!_hosts.IsAllowed(environment.FinOpsUrl) || schema.EnvironmentId != environment.ProviderEnvironmentId
            || !string.Equals(schema.EnvironmentOrigin, environment.FinOpsUrl.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            throw new DataAuthorizationException("Setup environment identity or host is not approved.");
        var session = await _authentication.GetCurrentSessionAsync(environment.TenantId, cancellationToken).ConfigureAwait(false);
        if (session is null || session.HomeAccountId != schema.AccountId
            || !string.Equals(session.TenantId, environment.TenantId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(session.TenantId, schema.TenantId, StringComparison.OrdinalIgnoreCase))
            throw new DataAuthorizationException("The account or tenant changed. Revalidate connections before loading setup.");
    }
}