using D365FO.Compare.Domain.DataComparison;
using D365FO.Compare.Domain.Metadata;

namespace D365FO.Compare.Application.DataComparison;

public static class FieldSelectionPolicy
{
    public static IReadOnlyList<AvailableFieldSelection> Resolve(EntityDataPlan plan, IReadOnlyList<FieldSchema> fields)
    {
        if (plan.AvailableFields is not null) return plan.AvailableFields;
        var excluded = plan.ExcludedFields.ToHashSet(StringComparer.Ordinal);
        return fields.Select(property => new AvailableFieldSelection(property,
            excluded.Count != 0 && (!excluded.Contains(property.Name) || plan.KeyFields.Contains(property.Name, StringComparer.Ordinal)
                || property.Name == plan.CompanyField))).ToArray();
    }

    public static DataComparisonProfile Migrate(DataComparisonProfile profile, SchemaSnapshot source)
    {
        if (profile.Version is not (1 or 2 or 3)) throw new InvalidDataException("The data comparison profile is incompatible.");
        if (profile.Version == 3) return profile;
        if (profile.Version == 2) return profile with { Version = 3 };
        var fieldsByEntity = source.Entities.ToDictionary(entity => entity.EntitySetName, entity => entity.Fields, StringComparer.Ordinal);
        return profile with
        {
            Version = 3,
            Entities = profile.Entities.Select(plan => plan with
            {
                AvailableFields = Resolve(plan, fieldsByEntity.GetValueOrDefault(plan.EntitySetName) ?? [])
            }).ToArray()
        };
    }
}