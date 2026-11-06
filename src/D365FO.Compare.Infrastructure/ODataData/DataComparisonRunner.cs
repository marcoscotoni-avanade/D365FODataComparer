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
            DataFailureDiagnostic? diagnostic = null;
            var side = "Source";
            var stage = "Reading data";
            var pageNumber = 0;
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
                    stage = "Saving data page";
                    pageNumber++;
                    await store.AddPageAsync(plan.EntitySetName, "Source", page, cancellationToken).ConfigureAwait(false);
                    sourceRows += page.Count;
                    stage = "Reading data";
                }
                side = "Target";
                pageNumber = 0;
                await foreach (var page in reader.ReadPagesAsync(request.Target, request.TargetSchema, prepared, request.Profile.TargetCompany,
                    limits, progress, "Target", cancellationToken).ConfigureAwait(false))
                {
                    stage = "Saving data page";
                    pageNumber++;
                    await store.AddPageAsync(plan.EntitySetName, "Target", page, cancellationToken).ConfigureAwait(false);
                    targetRows += page.Count;
                    stage = "Reading data";
                }
                side = "Both";
                stage = "Matching records";
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
                        stage = "Saving comparison results";
                        await store.AddResultsAsync(plan.EntitySetName, batch, cancellationToken).ConfigureAwait(false);
                        batch.Clear();
                        stage = "Matching records";
                    }
                }
                if (batch.Count > 0)
                {
                    stage = "Saving comparison results";
                    await store.AddResultsAsync(plan.EntitySetName, batch, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                status = DataEntityStatus.Canceled;
                message = "Run canceled. This entity has no conclusive data comparison.";
                stop = true;
            }
            catch (DataAuthorizationException exception) { status = DataEntityStatus.Failed; message = exception.Message; stop = true; }
            catch (DataLimitException exception) { status = DataEntityStatus.LimitReached; message = exception.Message; }
            catch (DataReadDiagnosticException exception)
            {
                diagnostic = exception.Diagnostic;
                status = exception.IsLimitFailure ? DataEntityStatus.LimitReached : DataEntityStatus.Failed;
                stop = exception.IsAuthorizationFailure;
                message = $"{diagnostic.Side}: {diagnostic.Stage} failed ({diagnostic.ExceptionType}" +
                    (diagnostic.HttpStatus is null ? ")." : $", HTTP {diagnostic.HttpStatus}).");
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException
                or InvalidOperationException or FormatException or HttpRequestException or OperationCanceledException)
            {
                status = DataEntityStatus.Failed;
                var json = exception as System.Text.Json.JsonException;
                diagnostic = new(side, stage, side == "Both" ? null : stage == "Reading data" ? pageNumber + 1 : pageNumber,
                    exception.GetType().Name, DataErrorDetails.Message(exception.Message),
                    JsonPath: json?.Path, LineNumber: json?.LineNumber, BytePosition: json?.BytePositionInLine,
                    InnerExceptionType: exception.InnerException?.GetType().Name);
                message = $"{side}: {stage} failed ({exception.GetType().Name}).";
            }
            results.Add(new(plan.EntitySetName, status, sourceRows, targetRows, counts[0], counts[1], counts[2], counts[3],
                counts[4] + counts[5] + counts[6], started, DateTimeOffset.UtcNow, message, diagnostic));
            progress?.Report(new(plan.EntitySetName, "Both", status.ToString(), sourceRows + targetRows));
        }
        return run with { FinishedUtc = DateTimeOffset.UtcNow, Entities = results };
    }
}