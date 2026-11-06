using D365FO.Compare.Application.DataComparison;
using D365FO.Compare.Domain.DataComparison;

namespace D365FO.Compare.Infrastructure.ODataData;

public sealed class DataComparisonRunner(IEntityDataReader reader, EntityDataEligibilityService eligibility, DataComparisonService comparer) : IDataComparisonRunner
{
    public async Task<DataRunManifest> RunAsync(DataComparisonRequest request, IDataRunStore store,
        IProgress<DataReadProgress>? progress, CancellationToken cancellationToken)
    {
        var limits = request.Profile.Limits;
        if (limits.MaxRows < 1 || limits.MaxPages < 1 || limits.MaxPageBytes < 1 || limits.MaxPageBytes > 128L * 1024 * 1024
            || limits.MaxSpoolBytes < 65536 || limits.MaxEntities < 1 || request.Profile.Entities.Count == 0 || request.Profile.Entities.Count > limits.MaxEntities)
            throw new InvalidOperationException("The data run selection or limits are invalid.");
        if (request.Profile.Entities.Select(plan => plan.EntitySetName).Distinct(StringComparer.Ordinal).Count() != request.Profile.Entities.Count)
            throw new InvalidOperationException("Duplicate entities are not allowed in a data run.");
        if (request.SourceSchema.AccountId != request.TargetSchema.AccountId || request.SourceSchema.TenantId != request.TargetSchema.TenantId)
            throw new DataAuthorizationException("Data schemas must belong to the same account and tenant.");
        var run = new DataRunManifest(Guid.NewGuid(), request, DateTimeOffset.UtcNow, null, []);
        var results = new List<DataEntityRunResult>();
        var stop = false;
        foreach (var plan in request.Profile.Entities)
        {
            var started = DateTimeOffset.UtcNow;
            var counts = new long[7];
            long sourceRows = 0;
            long targetRows = 0;
            var status = DataEntityStatus.Completed;
            string? message = null;
            var prepared = eligibility.Prepare(request.SourceSchema, request.TargetSchema, plan);
            if (stop || !prepared.IsReady)
            {
                results.Add(new(plan.EntitySetName, DataEntityStatus.Blocked, 0, 0, 0, 0, 0, 0, 0, started, DateTimeOffset.UtcNow,
                    stop ? "Not attempted after cancellation or authorization failure." : string.Join(" ", prepared.Problems)));
                continue;
            }
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await foreach (var page in reader.ReadPagesAsync(request.Source, request.SourceSchema, prepared, request.Profile.SourceCompany,
                    limits, progress, "Source", cancellationToken).ConfigureAwait(false))
                {
                    await store.AddPageAsync(plan.EntitySetName, "Source", page, cancellationToken).ConfigureAwait(false);
                    sourceRows += page.Count;
                }
                await foreach (var page in reader.ReadPagesAsync(request.Target, request.TargetSchema, prepared, request.Profile.TargetCompany,
                    limits, progress, "Target", cancellationToken).ConfigureAwait(false))
                {
                    await store.AddPageAsync(plan.EntitySetName, "Target", page, cancellationToken).ConfigureAwait(false);
                    targetRows += page.Count;
                }
                progress?.Report(new(plan.EntitySetName, "Both", "Matching complete acquisitions", sourceRows + targetRows));
                var batch = new List<DataRecordResult>(100);
                await foreach (var group in store.MatchAsync(plan.EntitySetName, cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = comparer.Compare(group.Source, group.Target);
                    counts[(int)result.Status]++;
                    batch.Add(result);
                    if (batch.Count == 100)
                    {
                        await store.AddResultsAsync(plan.EntitySetName, batch, cancellationToken).ConfigureAwait(false);
                        batch.Clear();
                    }
                }
                if (batch.Count > 0) await store.AddResultsAsync(plan.EntitySetName, batch, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                status = DataEntityStatus.Canceled;
                message = "Run canceled. This entity has no conclusive data comparison.";
                stop = true;
            }
            catch (DataAuthorizationException exception) { status = DataEntityStatus.Failed; message = exception.Message; stop = true; }
            catch (DataLimitException exception) { status = DataEntityStatus.LimitReached; message = exception.Message; }
            catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException
                or InvalidOperationException or FormatException or HttpRequestException or OperationCanceledException)
            {
                status = DataEntityStatus.Failed;
                message = exception is InvalidOperationException ? exception.Message : "The entity acquisition or comparison failed. Verify entity access, scope and payload compatibility.";
            }
            results.Add(new(plan.EntitySetName, status, sourceRows, targetRows, counts[0], counts[1], counts[2], counts[3],
                counts[4] + counts[5] + counts[6], started, DateTimeOffset.UtcNow, message));
            progress?.Report(new(plan.EntitySetName, "Both", status.ToString(), sourceRows + targetRows));
        }
        return run with { FinishedUtc = DateTimeOffset.UtcNow, Entities = results };
    }
}