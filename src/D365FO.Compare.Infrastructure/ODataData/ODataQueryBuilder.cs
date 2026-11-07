using System.Text.Json;
using D365FO.Compare.Domain.DataComparison;
using D365FO.Compare.Domain.Metadata;

namespace D365FO.Compare.Infrastructure.ODataData;

public sealed class ODataQueryBuilder(RecordValueCanonicalizer canonicalizer)
{
    public Uri Build(Uri origin, PreparedEntityPlan plan, SchemaSnapshot schema, string company)
    {
        if (!plan.IsReady) throw new InvalidOperationException("The entity data plan is not eligible.");
        var parameters = new List<string>();
        if (plan.Plan.AvailableFields?.Any(item => item.IsSelected) == true)
            parameters.Add("$select=" + Uri.EscapeDataString(string.Join(',', plan.Fields.Select(property => property.Name))));
        AddScopeAndFilters(parameters, plan.Plan, schema, company);
        var query = parameters.Count == 0 ? "" : "?" + string.Join('&', parameters);
        return new Uri(origin, "/data/" + Uri.EscapeDataString(plan.Plan.EntitySetName) + query);
    }

    public Uri BuildCount(Uri origin, EntityDataPlan plan, SchemaSnapshot schema, string company)
    {
        if (!schema.Entities.Any(entity => entity.EntitySetName == plan.EntitySetName))
            throw new InvalidOperationException("The entity is not present in the current schema.");
        var parameters = new List<string> { "$count=true", "$top=0" };
        AddScopeAndFilters(parameters, plan, schema, company);
        var query = parameters.Count == 0 ? "" : "?" + string.Join('&', parameters);
        return new Uri(origin, "/data/" + Uri.EscapeDataString(plan.EntitySetName) + query);
    }

    private void AddScopeAndFilters(List<string> parameters, EntityDataPlan plan, SchemaSnapshot schema, string company)
    {
        var entity = schema.Entities.SingleOrDefault(item => item.EntitySetName == plan.EntitySetName)
            ?? throw new InvalidOperationException("The entity is not present in the current schema.");
        var filters = new List<string>();
        if (plan.Scope == DataScopeKind.Company)
        {
            if (string.IsNullOrWhiteSpace(company)) throw new InvalidOperationException("Both company codes must be explicitly configured.");
            var companyField = entity.Fields.SingleOrDefault(property => property.Name == plan.CompanyField && property.TypeFullName == "Edm.String")
                ?? throw new InvalidOperationException("The company field must be a string field in the current schema.");
            filters.Add(companyField.Name + " eq " + Quote(company));
            parameters.Add("cross-company=true");
        }
        foreach (var filter in plan.Filters)
        {
            var property = entity.Fields.SingleOrDefault(property => property.Name == filter.Field)
                ?? throw new InvalidOperationException("A data filter field is not present in the current schema.");
            using var json = JsonDocument.Parse(filter.JsonValue);
            var value = canonicalizer.Canonicalize(property, json.RootElement, schema);
            if (value.State is RecordValueState.Invalid or RecordValueState.Missing) throw new InvalidOperationException("A typed data filter has an invalid value.");
            var literal = value.State == RecordValueState.Null ? "null" : property.EnumTypeFullName is { } enumName
                ? enumName + Quote(value.Original!) : property.TypeFullName == "Edm.String" ? Quote(value.Original!)
                : value.Original!;
            filters.Add(property.Name + " " + filter.Operator + " " + literal);
        }
        if (filters.Count != 0) parameters.Add("$filter=" + Uri.EscapeDataString(string.Join(" and ", filters.Select(filter => "(" + filter + ")"))));
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}