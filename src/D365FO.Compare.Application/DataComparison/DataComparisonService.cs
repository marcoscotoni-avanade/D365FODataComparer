using D365FO.Compare.Domain.DataComparison;

namespace D365FO.Compare.Application.DataComparison;

public sealed class DataComparisonService
{
    public DataRecordResult Compare(IReadOnlyList<DataRecord> source, IReadOnlyList<DataRecord> target)
    {
        var left = source.FirstOrDefault();
        var right = target.FirstOrDefault();
        var key = left?.Key ?? right?.Key ?? "Invalid key";
        if (source.Concat(target).Any(record => record.Key is null))
            return new(key, DataRecordStatus.InvalidKey, [], left?.Values, right?.Values);
        if (source.Concat(target).Any(record => record.Key != key))
            return new(key, DataRecordStatus.Inconclusive, [], left?.Values, right?.Values);
        if (source.Count > 1 || target.Count > 1)
            return new(key, DataRecordStatus.DuplicateKey, [], left?.Values, right?.Values);
        var invalidValues = source.Concat(target).Any(record => record.Values.Values.Any(value => value.State is RecordValueState.Invalid or RecordValueState.Missing));
        if (left is null || right is null)
            return new(key, invalidValues ? DataRecordStatus.Inconclusive : left is null ? DataRecordStatus.OnlyTarget : DataRecordStatus.OnlySource, [], left?.Values, right?.Values);
        var differences = new List<DataValueDifference>();
        foreach (var name in left.Values.Keys.Union(right.Values.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            left.Values.TryGetValue(name, out var before);
            right.Values.TryGetValue(name, out var after);
            if (before is null || after is null || before.State != after.State || before.EdmType != after.EdmType || before.Canonical != after.Canonical
                || before.State is RecordValueState.Invalid or RecordValueState.Missing)
                differences.Add(new(name, before, after));
        }
        return new(key, invalidValues ? DataRecordStatus.Inconclusive : differences.Count == 0 ? DataRecordStatus.Equal : DataRecordStatus.Different,
            differences, left.Values, right.Values);
    }
}