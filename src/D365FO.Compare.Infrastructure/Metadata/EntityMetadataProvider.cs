using D365FO.Compare.Application.Authentication;
using D365FO.Compare.Application.Metadata;
using D365FO.Compare.Application.Validation;
using D365FO.Compare.Domain.Authentication;
using D365FO.Compare.Domain.Environments;
using D365FO.Compare.Domain.Metadata;
using D365FO.Compare.Infrastructure.FoHttp;
using D365FO.Compare.Infrastructure.ODataEdm;
using D365FO.Compare.Infrastructure.RestMetadata;

namespace D365FO.Compare.Infrastructure.Metadata;

public sealed class MetadataWindowContext
{
    public Func<IntPtr> ParentWindowProvider { get; set; } = static () => IntPtr.Zero;
}

public sealed class EntityMetadataProvider(
    FoMetadataHttpClient http,
    DataEntitiesClient catalog,
    CsdlSchemaParser parser,
    EntityCatalogCorrelator correlator,
    IAuthenticationService authentication,
    IFinOpsHostPolicy hostPolicy,
    MetadataWindowContext windowContext) : IEntityMetadataProvider
{
    public async Task<SchemaSnapshot> CreateSnapshotAsync(EnvironmentConnection environment,
        IProgress<MetadataProgress>? progress, CancellationToken cancellationToken)
    {
        var session = await ValidateSessionAsync(environment, null, cancellationToken).ConfigureAwait(false);
        var origin = new Uri(environment.FinOpsUrl.GetLeftPart(UriPartial.Authority).ToLowerInvariant());
        var token = await authentication.GetAccessTokenAsync([origin.AbsoluteUri.TrimEnd('/') + "/.default"],
            windowContext.ParentWindowProvider(), cancellationToken).ConfigureAwait(false);
        await ValidateSessionAsync(environment, session.HomeAccountId, cancellationToken).ConfigureAwait(false);
        ParsedSchema schema;
        await using (var file = await http.DownloadAsync(new Uri(origin, "/data/$metadata"), token, "/data/$metadata",
            "application/xml", progress, cancellationToken).ConfigureAwait(false))
        {
            progress?.Report(new MetadataProgress("Parsing OData schema"));
            schema = await Task.Run(() => parser.Parse(file, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        await ValidateSessionAsync(environment, session.HomeAccountId, cancellationToken).ConfigureAwait(false);
        token = await authentication.GetAccessTokenAsync([origin.AbsoluteUri.TrimEnd('/') + "/.default"],
            windowContext.ParentWindowProvider(), cancellationToken).ConfigureAwait(false);
        await ValidateSessionAsync(environment, session.HomeAccountId, cancellationToken).ConfigureAwait(false);
        var entries = await catalog.LoadAsync(origin, token, progress, cancellationToken).ConfigureAwait(false);
        progress?.Report(new MetadataProgress("Correlating FO catalog"));
        var correlation = correlator.Correlate(schema.Entities, entries);
        await ValidateSessionAsync(environment, session.HomeAccountId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new SchemaSnapshot(session.TenantId, session.HomeAccountId, environment.ProviderEnvironmentId,
            environment.DisplayName, origin.GetLeftPart(UriPartial.Authority), DateTimeOffset.UtcNow,
            correlation.Entities, schema.Enums, schema.ComplexTypes, entries, correlation.Diagnostics);
        return snapshot with { Fingerprint = SchemaFingerprint.Compute(snapshot) };
    }

    private async Task<UserSession> ValidateSessionAsync(EnvironmentConnection environment, string? expectedAccountId,
        CancellationToken cancellationToken)
    {
        if (!hostPolicy.IsAllowed(environment.FinOpsUrl))
            throw new InvalidOperationException("The metadata environment is not approved by the host policy.");
        var session = await authentication.GetCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
        if (session is null || !string.Equals(session.TenantId, environment.TenantId, StringComparison.OrdinalIgnoreCase)
            || expectedAccountId is not null && session.HomeAccountId != expectedAccountId)
            throw new InvalidOperationException("The signed-in account changed or does not belong to this environment's tenant.");
        return session;
    }
}