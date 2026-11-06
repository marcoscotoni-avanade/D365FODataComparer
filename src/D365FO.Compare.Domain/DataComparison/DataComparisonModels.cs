using D365FO.Compare.Domain.Environments;
using D365FO.Compare.Domain.Metadata;

namespace D365FO.Compare.Domain.DataComparison;

public enum DataScopeKind { NotConfigured, Company, Shared }
public enum RecordValueState { Present, Null, Missing, Invalid }
public enum DataRecordStatus { Equal, Different, OnlySource, OnlyTarget, DuplicateKey, InvalidKey, Inconclusive }
public enum DataEntityStatus { Pending, Completed, Failed, Canceled, LimitReached, Blocked }
public sealed record RecordValue(RecordValueState State, string EdmType, string? Canonical, string? Original);
public sealed record DataFilter(string Field, string Operator, string JsonValue);
public sealed record EntityDataPlan(string EntitySetName, DataScopeKind Scope, string? CompanyField,
    IReadOnlyList<string> KeyFields, IReadOnlyList<string> ExcludedFields, IReadOnlyList<DataFilter> Filters);
public sealed record DataReadLimits(long MaxRows = 100000, int MaxPages = 1000, long MaxPageBytes = 33554432,
    long MaxSpoolBytes = 2147483648, int MaxEntities = 100);
public sealed record DataComparisonProfile(string SourceCompany, string TargetCompany, IReadOnlyList<EntityDataPlan> Entities,
    DataReadLimits Limits, int Version = 1, IReadOnlyList<string>? SkippedEntities = null,
    bool AllEligible = false, IReadOnlyList<string>? SelectedEntities = null);
public sealed record DataComparisonRequest(EnvironmentConnection Source, EnvironmentConnection Target,
    SchemaSnapshot SourceSchema, SchemaSnapshot TargetSchema, DataComparisonProfile Profile);
public sealed record PreparedEntityPlan(EntityDataPlan Plan, IReadOnlyList<FieldSchema> Fields, IReadOnlyList<string> Problems)
{
    public bool IsReady => Problems.Count == 0;
}
public sealed record DataRecord(IReadOnlyDictionary<string, RecordValue> Values, string? Key);
public sealed record DataValueDifference(string Field, RecordValue? Source, RecordValue? Target);
public sealed record DataRecordResult(string Key, DataRecordStatus Status,
    IReadOnlyList<DataValueDifference> Differences, IReadOnlyDictionary<string, RecordValue>? Source,
    IReadOnlyDictionary<string, RecordValue>? Target);
public sealed record DataReadProgress(string Entity, string Side, string Stage, long Rows = 0, int Pages = 0, long Bytes = 0);
public sealed record DataEntityRunResult(string EntitySetName, DataEntityStatus Status, long SourceRows, long TargetRows,
    long Equal, long Different, long OnlySource, long OnlyTarget, long Inconclusive, DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc, string? Message = null)
{
    public bool IsComplete => Status == DataEntityStatus.Completed && Inconclusive == 0;
}
public sealed record DataRunManifest(Guid RunId, DataComparisonRequest Request, DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc, IReadOnlyList<DataEntityRunResult> Entities)
{
    public bool IsComplete => FinishedUtc is not null && (Request.Profile.SkippedEntities?.Count ?? 0) == 0
        && Entities.Count == Request.Profile.Entities.Count && Entities.All(entity => entity.IsComplete);
    public bool AreEqual => IsComplete && Entities.All(entity => entity.Different + entity.OnlySource + entity.OnlyTarget == 0);
}