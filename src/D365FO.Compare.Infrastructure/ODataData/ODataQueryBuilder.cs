using System.Text.Json;
using D365FO.Compare.Domain.DataComparison;
using D365FO.Compare.Domain.Metadata;

namespace D365FO.Compare.Infrastructure.ODataData;

public sealed class ODataQueryBuilder(RecordValueCanonicalizer canonicalizer)
{
    public Uri Build(Uri origin, PreparedEntityPlan plan, SchemaSnapshot schema, string company)
    {
        if (!plan.IsReady) throw new InvalidOperationException("The entity data plan is not eligible.");
        var select = string.Join(',', plan.Fields.Select(property => property.Name));
        var filters = new List<string>();
        if (plan.Plan.Scope == DataScopeKind.Company)
        {
            if (string.IsNullOrWhiteSpace(company)) throw new InvalidOperationException("Both company codes must be explicitly configured.");
            filters.Add(plan.Plan.CompanyField + " eq " + Quote(company));
        }
        foreach (var filter in plan.Plan.Filters)
        {
            var property = schema.Entities.Single(entity => entity.EntitySetName == plan.Plan.EntitySetName).Fields.Single(property => property.Name == filter.Field);
            using var json = JsonDocument.Parse(filter.JsonValue);
            var value = canonicalizer.Canonicalize(property, json.RootElement, schema);
            if (value.State is RecordValueState.Invalid or RecordValueState.Missing) throw new InvalidOperationException("A typed data filter has an invalid value.");
            var literal = value.State == RecordValueState.Null ? "null" : property.EnumTypeFullName is { } enumName
                ? enumName + Quote(value.Original!) : property.TypeFullName == "Edm.String" ? Quote(value.Original!)
                : value.Original!;
            filters.Add(property.Name + " " + filter.Operator + " " + literal);
        }
        var query = "$select=" + Uri.EscapeDataString(select);
        if (plan.Plan.Scope == DataScopeKind.Company) query += "&cross-company=true";
        if (filters.Count != 0) query += "&$filter=" + Uri.EscapeDataString(string.Join(" and ", filters.Select(filter => "(" + filter + ")")));
        return new Uri(origin, "/data/" + Uri.EscapeDataString(plan.Plan.EntitySetName) + "?" + query);
    }
    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}