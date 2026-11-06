using D365FO.Compare.Application.DataComparison;
using D365FO.Compare.Domain.DataComparison;
using D365FO.Compare.Domain.Environments;
using D365FO.Compare.Domain.Metadata;
using D365FO.Compare.Infrastructure.FoHttp;

namespace D365FO.Compare.Infrastructure.ODataData;

public sealed class DataComparisonSetupProvider(ILegalEntityProvider companies) : IDataComparisonSetupProvider
{
    public async Task<DataComparisonSetupSnapshot> LoadAsync(ComparisonContextState context, IProgress<DataReadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!context.OnlineReady || context.Source is null || context.Target is null || context.SourceSchema is null || context.TargetSchema is null)
            throw new DataAuthorizationException("Validate connections and load both schemas before loading setup.");
        var source = await LoadSideAsync(context.Source, context.SourceSchema, "Source", progress, cancellationToken).ConfigureAwait(false);
        if (source.RequiresAuthentication)
            return new(source, new([], false, DateTimeOffset.UtcNow, new("Target", "Load setup", null, "NotAttempted", "Source authentication failed; Target was not requested."), true));
        var target = await LoadSideAsync(context.Target, context.TargetSchema, "Target", progress, cancellationToken).ConfigureAwait(false);
        return new(source, target);
    }

    private async Task<CompanySetupResult> LoadSideAsync(EnvironmentConnection environment, SchemaSnapshot schema, string side,
        IProgress<DataReadProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            var result = await companies.LoadAsync(environment, schema, progress, side, cancellationToken).ConfigureAwait(false);
            return new(result, true, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException
            or TimeoutException or HttpRequestException or InvalidOperationException or OperationCanceledException)
        {
            var http = exception as MetadataEndpointException;
            var requiresAuthentication = exception is DataAuthorizationException || http?.StatusCode == System.Net.HttpStatusCode.Unauthorized;
            return new([], false, DateTimeOffset.UtcNow, new(side, "Load LegalEntities setup", null, exception.GetType().Name,
                DataErrorDetails.Message(exception.Message), http is null ? null : (int)http.StatusCode, DataErrorDetails.Identifier(http?.RequestId)), requiresAuthentication);
        }
    }
}