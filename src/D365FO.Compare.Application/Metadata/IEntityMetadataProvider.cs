using D365FO.Compare.Domain.Environments;
using D365FO.Compare.Domain.Metadata;

namespace D365FO.Compare.Application.Metadata;

public sealed record MetadataProgress(string Stage, long BytesReceived = 0, long? TotalBytes = null);

public interface IEntityMetadataProvider
{
    Task<SchemaSnapshot> CreateSnapshotAsync(EnvironmentConnection environment, IProgress<MetadataProgress>? progress,
        CancellationToken cancellationToken);
}

public interface ISchemaSnapshotRepository
{
    Task<SchemaSnapshot?> LoadAsync(EnvironmentConnection environment, string accountId, CancellationToken cancellationToken);
    Task SaveAsync(SchemaSnapshot snapshot, CancellationToken cancellationToken);
    Task DeleteAsync(EnvironmentConnection environment, string accountId, CancellationToken cancellationToken);
}