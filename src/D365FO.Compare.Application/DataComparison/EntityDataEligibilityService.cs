using System.Text.Json;
using D365FO.Compare.Domain.DataComparison;
using D365FO.Compare.Domain.Metadata;

namespace D365FO.Compare.Application.DataComparison;

public sealed class EntityDataEligibilityService
{
    public static bool IsSupported(FieldSchema property, SchemaSnapshot schema) => !property.IsCollection
        && (property.TypeFullName is "Edm.String" or "Edm.Boolean" or "Edm.Byte" or "Edm.SByte" or "Edm.Int16" or "Edm.Int32"
            or "Edm.Int64" or "Edm.Decimal" or "Edm.Double" or "Edm.Single" or "Edm.Guid" or "Edm.Date"
            or "Edm.DateTimeOffset" or "Edm.TimeOfDay" or "Edm.Duration"
            || schema.Enums.Any(type => type.FullName == property.TypeFullName));

    public static bool HaveMatchingODataKeys(EntitySchema source, EntitySchema target)
    {
        var sourceKeys = source.Key.Select(key => key.Name).ToArray();
        var targetKeys = target.Key.Select(key => key.Name).ToArray();
        return sourceKeys.Length > 0
            && sourceKeys.Length == sourceKeys.Distinct(StringComparer.Ordinal).Count()
            && targetKeys.Length == targetKeys.Distinct(StringComparer.Ordinal).Count()
            && sourceKeys.Length == targetKeys.Length
            && sourceKeys.Order(StringComparer.Ordinal).SequenceEqual(targetKeys.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    public PreparedEntityPlan Prepare(SchemaSnapshot source, SchemaSnapshot target, EntityDataPlan plan)
    {
        var problems = new List<string>();
        var before = source.Entities.SingleOrDefault(entity => entity.EntitySetName == plan.EntitySetName);
        var after = target.Entities.SingleOrDefault(entity => entity.EntitySetName == plan.EntitySetName);
        if (before is null || after is null) return new(plan, [], ["EntitySet is not present in both schemas."]);
        var sourceKey = before.Key.Select(key => key.Name).ToArray();
        var keys = plan.KeyFields.Count == 0 ? sourceKey : plan.KeyFields.ToArray();
        if (!HaveMatchingODataKeys(before, after))
            problems.Add("OData key fields must have the same names in Source and Target.");
        if (keys.Length == 0 || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
            problems.Add("Configure a nonempty key without duplicate fields.");
        if (plan.Scope == DataScopeKind.NotConfigured) problems.Add("Confirm Company or Shared scope.");
        if (plan.Scope == DataScopeKind.Company && (string.IsNullOrWhiteSpace(plan.CompanyField)
            || !before.Fields.Any(property => property.Name == plan.CompanyField && property.TypeFullName == "Edm.String")
            || !after.Fields.Any(property => property.Name == plan.CompanyField && property.TypeFullName == "Edm.String")))
            problems.Add("Company scope requires an explicit string company field on both sides.");
        var excluded = plan.ExcludedFields.ToHashSet(StringComparer.Ordinal);
        if (keys.Any(excluded.Contains)) problems.Add("Key fields cannot be excluded.");
        var left = before.Fields.ToDictionary(property => property.Name, StringComparer.Ordinal);
        var right = after.Fields.ToDictionary(property => property.Name, StringComparer.Ordinal);
        var available = FieldSelectionPolicy.Resolve(plan, before.Fields);
        var selected = available.Where(item => item.IsSelected).Select(item => item.Field.Name).ToHashSet(StringComparer.Ordinal);
        if (available.Select(item => item.Field.Name).Distinct(StringComparer.Ordinal).Count() != available.Count)
            problems.Add("The available field catalog contains duplicate names.");
        foreach (var item in available)
            if (!left.TryGetValue(item.Field.Name, out var current) || current.TypeFullName != item.Field.TypeFullName || current.IsCollection != item.Field.IsCollection)
                problems.Add($"Saved field {item.Field.Name} does not match the current Source schema.");
        if (selected.Count != 0 && selected.All(excluded.Contains))
            problems.Add("All selected fields are excluded. Select an allowed field or clear the field selection for full mode.");
        foreach (var key in keys)
            if (!left.ContainsKey(key) || !right.ContainsKey(key)) problems.Add($"Key field {key} is missing.");
        foreach (var name in excluded)
            if (!left.ContainsKey(name) && !right.ContainsKey(name)) problems.Add($"Excluded field {name} is not in either schema.");
        var fields = new List<FieldSchema>();
        foreach (var name in left.Keys.Union(right.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (selected.Count != 0 && !selected.Contains(name) && !keys.Contains(name, StringComparer.Ordinal) && name != plan.CompanyField) continue;
            if (excluded.Contains(name) && !keys.Contains(name, StringComparer.Ordinal) && name != plan.CompanyField) continue;
            if (!left.TryGetValue(name, out var sourceProperty) || !right.TryGetValue(name, out var targetProperty))
            {
                problems.Add($"Field {name} is missing on one side. Exclude it explicitly.");
                continue;
            }
            if (sourceProperty.TypeFullName != targetProperty.TypeFullName || !IsSupported(sourceProperty, source) || !IsSupported(targetProperty, target))
                problems.Add($"Field {name} has incompatible or unsupported types. Exclude it explicitly.");
            else if (sourceProperty.EnumTypeFullName is { } enumName)
            {
                var sourceEnum = source.Enums.SingleOrDefault(type => type.FullName == enumName);
                var targetEnum = target.Enums.SingleOrDefault(type => type.FullName == enumName);
                if (JsonSerializer.Serialize(sourceEnum) != JsonSerializer.Serialize(targetEnum))
                    problems.Add($"Enum {enumName} differs between schemas.");
            }
            fields.Add(sourceProperty);
        }
        foreach (var filter in plan.Filters)
            if (!left.TryGetValue(filter.Field, out var property) || !right.TryGetValue(filter.Field, out var targetProperty)
                || property.TypeFullName != targetProperty.TypeFullName || !IsSupported(property, source)
                || filter.Operator is not ("eq" or "ne" or "gt" or "ge" or "lt" or "le")
                || property.EnumTypeFullName is not null && filter.Operator is not ("eq" or "ne"))
                problems.Add($"Filter on {filter.Field} is invalid.");
        return new PreparedEntityPlan(plan with { KeyFields = keys, AvailableFields = available }, fields, problems);
    }
}