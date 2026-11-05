using System.Globalization;
using System.Text.Json;
using D365FO.Compare.Application.Metadata;
using D365FO.Compare.Domain.Metadata;

namespace D365FO.Compare.Application.Comparison;

public sealed class SchemaComparisonService
{
    public SchemaComparisonResult Compare(SchemaSnapshot source, SchemaSnapshot target,
        IReadOnlyCollection<string>? selectedEntitySets = null, CancellationToken cancellationToken = default)
    {
        if (source.FormatVersion != target.FormatVersion || source.PolicyVersion != target.PolicyVersion)
            throw new InvalidOperationException("The snapshot versions or comparison policies are incompatible. Refresh both schemas.");
        if (selectedEntitySets is { Count: 0 }) throw new ArgumentException("Select at least one entity.", nameof(selectedEntitySets));
        var sourceEntities = source.Entities.ToDictionary(entity => entity.EntitySetName, StringComparer.Ordinal);
        var targetEntities = target.Entities.ToDictionary(entity => entity.EntitySetName, StringComparer.Ordinal);
        var all = sourceEntities.Keys.Union(targetEntities.Keys, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (selectedEntitySets is not null && selectedEntitySets.Any(name => !all.Contains(name)))
            throw new ArgumentException("The selection contains an entity missing from both snapshots.", nameof(selectedEntitySets));
        var names = selectedEntitySets is null ? all : selectedEntitySets.Distinct(StringComparer.Ordinal);
        var entities = new List<EntityComparison>();
        foreach (var name in names.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            sourceEntities.TryGetValue(name, out var before);
            targetEntities.TryGetValue(name, out var after);
            if (before is null || after is null)
            {
                var added = before is null;
                entities.Add(new EntityComparison(name, added ? EntityChangeKind.Added : EntityChangeKind.Removed,
                    added ? DifferenceSeverity.Additive : DifferenceSeverity.Breaking, true,
                    [new("Entity", "Presence", before?.EntityTypeFullName, after?.EntityTypeFullName,
                        added ? DifferenceSeverity.Additive : DifferenceSeverity.Breaking)]));
                continue;
            }
            var changes = new List<SchemaDifference>();
            var complete = true;
            void Different(string member, string property, string? left, string? right, DifferenceSeverity severity)
            {
                if (!string.Equals(left, right, StringComparison.Ordinal)) changes.Add(new(member, property, left, right, severity));
            }
            Different("Entity", "EntityType", before.EntityTypeFullName, after.EntityTypeFullName, DifferenceSeverity.Breaking);
            Different("Entity", "Ordered key", JsonSerializer.Serialize(before.Key), JsonSerializer.Serialize(after.Key), DifferenceSeverity.Breaking);
            CompareFields(before.Fields, after.Fields, "", source, target, changes, ref complete, new HashSet<string>(StringComparer.Ordinal), cancellationToken);
            Different("Entity", "Navigations", NavigationValue(before.Navigations), NavigationValue(after.Navigations), DifferenceSeverity.Informational);
            Different("Entity", "Annotations", AnnotationValue(before.Annotations), AnnotationValue(after.Annotations), DifferenceSeverity.Informational);
            var kind = changes.Count != 0 ? EntityChangeKind.Changed : complete ? EntityChangeKind.Unchanged : EntityChangeKind.Inconclusive;
            entities.Add(new EntityComparison(name, kind, changes.Count == 0 ? DifferenceSeverity.None : changes.Max(change => change.Severity), complete, changes));
        }
        return new SchemaComparisonResult(SchemaFingerprint.Compute(source), SchemaFingerprint.Compute(target),
            selectedEntitySets is null ? "All entities" : "Selected entities", entities);
    }

    private static void CompareFields(IReadOnlyList<FieldSchema> sourceFields, IReadOnlyList<FieldSchema> targetFields,
        string prefix, SchemaSnapshot source, SchemaSnapshot target, List<SchemaDifference> changes, ref bool complete,
        HashSet<string> visitedTypes, CancellationToken cancellationToken)
    {
        var left = sourceFields.ToDictionary(field => field.Name, StringComparer.Ordinal);
        var right = targetFields.ToDictionary(field => field.Name, StringComparer.Ordinal);
        foreach (var name in left.Keys.Union(right.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            left.TryGetValue(name, out var before);
            right.TryGetValue(name, out var after);
            var member = prefix + name;
            if (before is null || after is null)
            {
                changes.Add(new(member, "Presence", before?.TypeFullName, after?.TypeFullName,
                    before is null ? DifferenceSeverity.Additive : DifferenceSeverity.Breaking));
                continue;
            }
            if (before.TypeFullName != after.TypeFullName || before.IsCollection != after.IsCollection)
                changes.Add(new(member, "EDM type", TypeLabel(before), TypeLabel(after), DifferenceSeverity.Breaking));
            CompareFacet(member, "Nullable", before.Nullable, after.Nullable, changes, ref complete,
                (oldValue, newValue) => oldValue == "true" && newValue == "false" ? DifferenceSeverity.Breaking : DifferenceSeverity.Additive);
            if (before.TypeFullName is "Edm.String" or "Edm.Binary" || after.TypeFullName is "Edm.String" or "Edm.Binary")
                CompareFacet(member, "MaxLength", before.MaxLength, after.MaxLength, changes, ref complete, LengthSeverity);
            if (before.TypeFullName is "Edm.Decimal" or "Edm.DateTimeOffset" or "Edm.Duration" or "Edm.TimeOfDay"
                || after.TypeFullName is "Edm.Decimal" or "Edm.DateTimeOffset" or "Edm.Duration" or "Edm.TimeOfDay")
                CompareFacet(member, "Precision", before.Precision, after.Precision, changes, ref complete,
                    (oldValue, newValue) => before.TypeFullName == "Edm.Decimal" && after.TypeFullName == "Edm.Decimal"
                        ? DecimalSeverity(before, after) : LengthSeverity(oldValue, newValue));
            if (before.TypeFullName == "Edm.Decimal" || after.TypeFullName == "Edm.Decimal")
                CompareFacet(member, "Scale", before.Scale, after.Scale, changes, ref complete, (_, _) => DecimalSeverity(before, after));
            if (AnnotationValue(before.Annotations) != AnnotationValue(after.Annotations))
                changes.Add(new(member, "Annotations", AnnotationValue(before.Annotations), AnnotationValue(after.Annotations), DifferenceSeverity.Informational));
            if (before.EnumTypeFullName is { } enumName && enumName == after.EnumTypeFullName && visitedTypes.Add("enum:" + enumName))
                CompareEnum(member, source.Enums.SingleOrDefault(type => type.FullName == enumName), target.Enums.SingleOrDefault(type => type.FullName == enumName), changes, ref complete);
            var sourceComplex = source.ComplexTypes.SingleOrDefault(type => type.FullName == before.TypeFullName);
            var targetComplex = target.ComplexTypes.SingleOrDefault(type => type.FullName == after.TypeFullName);
            if (before.TypeFullName == after.TypeFullName && (sourceComplex is not null || targetComplex is not null)
                && visitedTypes.Add("complex:" + before.TypeFullName))
            {
                if (sourceComplex is null || targetComplex is null)
                {
                    complete = false;
                    changes.Add(new(member, "Complex type", sourceComplex?.FullName, targetComplex?.FullName, DifferenceSeverity.Informational, false));
                }
                else CompareFields(sourceComplex.Fields, targetComplex.Fields, member + ".", source, target, changes, ref complete, visitedTypes, cancellationToken);
            }
        }
    }

    private static void CompareFacet(string member, string facet, ObservedValue before, ObservedValue after,
        List<SchemaDifference> changes, ref bool complete, Func<string, string, DifferenceSeverity> severity)
    {
        if (before.State != ObservationState.Observed || after.State != ObservationState.Observed)
        {
            complete = false;
            if (before.State != after.State || before.EffectiveValue != after.EffectiveValue || before.State == ObservationState.Invalid)
                changes.Add(new(member, facet, before.ToString(), after.ToString(), DifferenceSeverity.Informational, false));
            return;
        }
        if (before.Value != after.Value)
            changes.Add(new(member, facet, before.Value, after.Value,
                before.Value is null || after.Value is null ? DifferenceSeverity.Informational : severity(before.Value, after.Value),
                before.Value is not null && after.Value is not null));
    }

    private static DifferenceSeverity LengthSeverity(string before, string after)
    {
        if (before == "max" && long.TryParse(after, out _)) return DifferenceSeverity.Breaking;
        if (after == "max" && long.TryParse(before, out _)) return DifferenceSeverity.Additive;
        return long.TryParse(before, out var oldLength) && long.TryParse(after, out var newLength)
            ? newLength < oldLength ? DifferenceSeverity.Breaking : DifferenceSeverity.Additive : DifferenceSeverity.ConstraintChange;
    }

    private static DifferenceSeverity DecimalSeverity(FieldSchema before, FieldSchema after)
    {
        if (before.Precision.State != ObservationState.Observed || after.Precision.State != ObservationState.Observed
            || before.Scale.State != ObservationState.Observed || after.Scale.State != ObservationState.Observed)
            return DifferenceSeverity.ConstraintChange;
        if (!int.TryParse(before.Precision.Value, out var oldPrecision) || !int.TryParse(after.Precision.Value, out var newPrecision)
            || !int.TryParse(before.Scale.Value, out var oldScale) || !int.TryParse(after.Scale.Value, out var newScale))
            return DifferenceSeverity.ConstraintChange;
        return newScale < oldScale || newPrecision - newScale < oldPrecision - oldScale
            ? DifferenceSeverity.Breaking : DifferenceSeverity.Additive;
    }

    private static void CompareEnum(string member, EnumSchema? before, EnumSchema? after, List<SchemaDifference> changes, ref bool complete)
    {
        if (before is null || after is null)
        {
            complete = false;
            changes.Add(new(member, "Enum definition", before?.FullName, after?.FullName, DifferenceSeverity.Informational, false));
            return;
        }
        if (before.UnderlyingType != after.UnderlyingType || before.IsFlags != after.IsFlags)
            changes.Add(new(member, "Enum type/flags", $"{before.UnderlyingType}/{before.IsFlags}", $"{after.UnderlyingType}/{after.IsFlags}", DifferenceSeverity.Breaking));
        var left = before.Members.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var right = after.Members.ToDictionary(item => item.Name, StringComparer.Ordinal);
        foreach (var name in left.Keys.Union(right.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            left.TryGetValue(name, out var oldMember);
            right.TryGetValue(name, out var newMember);
            if (oldMember?.Value != newMember?.Value)
                changes.Add(new(member, $"{before.FullName}.{name}", oldMember?.Value.ToString(CultureInfo.InvariantCulture),
                    newMember?.Value.ToString(CultureInfo.InvariantCulture), oldMember is null ? DifferenceSeverity.Additive : DifferenceSeverity.Breaking));
        }
    }

    private static string TypeLabel(FieldSchema field) => field.IsCollection ? $"Collection({field.TypeFullName})" : field.TypeFullName;
    private static string NavigationValue(IReadOnlyList<NavigationSchema> values) => JsonSerializer.Serialize(values.OrderBy(value => value.Name, StringComparer.Ordinal));
    private static string AnnotationValue(IReadOnlyList<AnnotationSchema> values) => JsonSerializer.Serialize(values.OrderBy(value => value.Term, StringComparer.Ordinal).ThenBy(value => value.Qualifier, StringComparer.Ordinal));
}