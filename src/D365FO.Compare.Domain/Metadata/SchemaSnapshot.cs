namespace D365FO.Compare.Domain.Metadata;

public enum ObservationState { Observed, Missing, Invalid }

public sealed record ObservedValue(
    ObservationState State,
    string? Value = null,
    string? EffectiveValue = null,
    string Origin = "ODataCsdl")
{
    public static ObservedValue Declared(string value) => new(ObservationState.Observed, value, value);
    public static ObservedValue Missing(string? effectiveValue = null) => new(ObservationState.Missing, null, effectiveValue);
    public override string ToString() => State == ObservationState.Observed ? Value ?? "null" : $"{State}";
}

public sealed record AnnotationSchema(string Term, string? Qualifier, string CanonicalValue);
public sealed record KeyFieldSchema(string Name, int Order, string? Alias = null);
public sealed record FieldSchema(
    string Name,
    string TypeFullName,
    bool IsCollection,
    ObservedValue Nullable,
    ObservedValue MaxLength,
    ObservedValue Precision,
    ObservedValue Scale,
    string? EnumTypeFullName,
    IReadOnlyList<AnnotationSchema> Annotations);
public sealed record NavigationSchema(
    string Name, string TargetType, bool IsCollection, ObservedValue Nullable, string? Partner);
public sealed record EnumMemberSchema(string Name, long Value);
public sealed record EnumSchema(string FullName, string UnderlyingType, bool IsFlags, IReadOnlyList<EnumMemberSchema> Members);
public sealed record ComplexTypeSchema(string FullName, IReadOnlyList<FieldSchema> Fields);
public enum CorrelationStatus { Exact, CollectionOnly, EntityNameOnly, Ambiguous, Unresolved }
public sealed record MetadataDiagnostic(string Code, string Subject, string Message);
public sealed record CatalogEntry(string? Name, string? PublicEntityName, string? PublicCollectionName, string? Category);

public sealed record EntitySchema(
    string EntitySetName,
    string EntityTypeFullName,
    IReadOnlyList<FieldSchema> Fields,
    IReadOnlyList<KeyFieldSchema> Key,
    IReadOnlyList<NavigationSchema> Navigations,
    IReadOnlyList<AnnotationSchema> Annotations,
    string? FoEntityName = null,
    CorrelationStatus Correlation = CorrelationStatus.Unresolved);

public sealed record SchemaSnapshot(
    string TenantId,
    string AccountId,
    string EnvironmentId,
    string EnvironmentName,
    string EnvironmentOrigin,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<EntitySchema> Entities,
    IReadOnlyList<EnumSchema> Enums,
    IReadOnlyList<ComplexTypeSchema> ComplexTypes,
    IReadOnlyList<CatalogEntry> Catalog,
    IReadOnlyList<MetadataDiagnostic> Diagnostics,
    string Fingerprint = "",
    int FormatVersion = 1,
    string PolicyVersion = "schema-v1");