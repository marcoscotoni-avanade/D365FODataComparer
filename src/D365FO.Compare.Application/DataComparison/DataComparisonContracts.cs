using D365FO.Compare.Domain.DataComparison;
using D365FO.Compare.Domain.Environments;
using D365FO.Compare.Domain.Metadata;

namespace D365FO.Compare.Application.DataComparison;

public interface IEntityDataReader
{
    IAsyncEnumerable<IReadOnlyList<DataRecord>> ReadPagesAsync(EnvironmentConnection environment, SchemaSnapshot schema,
        PreparedEntityPlan plan, string company, DataReadLimits limits, IProgress<DataReadProgress>? progress,
        string side, CancellationToken cancellationToken);
}
public interface IDataRunStore : IAsyncDisposable
{
    Task AddPageAsync(string entity, string side, IReadOnlyList<DataRecord> records, CancellationToken cancellationToken);
    IAsyncEnumerable<(IReadOnlyList<DataRecord> Source, IReadOnlyList<DataRecord> Target)> MatchAsync(string entity, CancellationToken cancellationToken);
    Task AddResultsAsync(string entity, IReadOnlyList<DataRecordResult> results, CancellationToken cancellationToken);
    Task<IReadOnlyList<DataRecordResult>> ReadResultsAsync(string entity, DataRecordStatus? status, long offset, int count, CancellationToken cancellationToken);
    Task<long> CountResultsAsync(string entity, DataRecordStatus? status, CancellationToken cancellationToken);
}
public interface IDataRunStoreFactory
{
    IDataRunStore Create(Guid runId, long maxBytes);
}
public interface IDataComparisonRunner
{
    Task<DataRunManifest> RunAsync(DataComparisonRequest request, IDataRunStore store, IProgress<DataReadProgress>? progress, CancellationToken cancellationToken);
}
public interface IDataComparisonProfileRepository
{
    Task SaveAsync(DataComparisonRequest request, CancellationToken cancellationToken);
    Task<DataComparisonProfile?> LoadAsync(SchemaSnapshot source, SchemaSnapshot target, CancellationToken cancellationToken);
}
public sealed class DataLimitException(string message) : IOException(message);
public sealed class DataAuthorizationException(string message) : IOException(message);
public sealed class DataReadDiagnosticException(DataFailureDiagnostic diagnostic, Exception innerException)
    : IOException(diagnostic.Reason, innerException)
{
    public DataFailureDiagnostic Diagnostic { get; } = diagnostic;
    public bool IsAuthorizationFailure => InnerException is DataAuthorizationException;
    public bool IsLimitFailure => InnerException is DataLimitException;
}