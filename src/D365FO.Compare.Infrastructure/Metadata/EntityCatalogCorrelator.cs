using D365FO.Compare.Domain.Metadata;

namespace D365FO.Compare.Infrastructure.Metadata;

public sealed class EntityCatalogCorrelator
{
    public (IReadOnlyList<EntitySchema> Entities, IReadOnlyList<MetadataDiagnostic> Diagnostics) Correlate(
        IReadOnlyList<EntitySchema> entities, IReadOnlyList<CatalogEntry> catalog)
    {
        var byCollection = catalog.Where(item => item.PublicCollectionName is not null).ToLookup(item => item.PublicCollectionName!, StringComparer.Ordinal);
        var byType = catalog.Where(item => item.PublicEntityName is not null).ToLookup(item => item.PublicEntityName!, StringComparer.Ordinal);
        var diagnostics = new List<MetadataDiagnostic>();
        var mapped = entities.Select(entity =>
        {
            var typeName = entity.EntityTypeFullName[(entity.EntityTypeFullName.LastIndexOf('.') + 1)..];
            var collections = byCollection[entity.EntitySetName].ToArray();
            var types = byType[typeName].ToArray();
            var candidates = collections.Concat(types).Distinct().ToArray();
            var exact = collections.Where(item => item.PublicEntityName == typeName).ToArray();
            var status = candidates.Length == 0 ? CorrelationStatus.Unresolved
                : candidates.Length > 1 || collections.Length > 1 || types.Length > 1 ? CorrelationStatus.Ambiguous
                : exact.Length == 1 ? CorrelationStatus.Exact
                : collections.Length == 1 ? CorrelationStatus.CollectionOnly : CorrelationStatus.EntityNameOnly;
            if (status != CorrelationStatus.Exact)
                diagnostics.Add(new MetadataDiagnostic(status.ToString(), entity.EntitySetName,
                    $"FO catalog mapping: {status}. Candidates: {string.Join(", ", candidates.Select(item => item.Name ?? "Unknown"))}"));
            return entity with { Correlation = status, FoEntityName = status == CorrelationStatus.Exact ? exact[0].Name : null };
        }).ToArray();
        return (mapped, diagnostics);
    }
}