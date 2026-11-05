namespace D365FO.Compare.Domain.Metadata;

public enum DifferenceSeverity { None, Informational, Additive, ConstraintChange, Breaking }
public enum EntityChangeKind { Unchanged, Added, Removed, Changed, Inconclusive }
public sealed record SchemaDifference(string Member, string Property, string? SourceValue, string? TargetValue,
    DifferenceSeverity Severity, bool IsConclusive = true);
public sealed record EntityComparison(string EntitySetName, EntityChangeKind Kind, DifferenceSeverity Severity,
    bool IsComplete, IReadOnlyList<SchemaDifference> Differences);
public sealed record SchemaComparisonResult(string SourceFingerprint, string TargetFingerprint, string Scope,
    IReadOnlyList<EntityComparison> Entities, string PolicyVersion = "schema-v1")
{
    public bool AreEquivalent => Entities.All(entity => entity.IsComplete && entity.Kind == EntityChangeKind.Unchanged);
    public bool HasBreakingChanges => Entities.Any(entity => entity.Severity == DifferenceSeverity.Breaking);
}