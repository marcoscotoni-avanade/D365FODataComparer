using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using D365FO.Compare.Domain.DataComparison;
using D365FO.Compare.Domain.Metadata;

namespace D365FO.Compare.Infrastructure.ODataData;

public sealed class RecordValueCanonicalizer
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    public RecordValue Canonicalize(FieldSchema property, JsonElement value, SchemaSnapshot schema)
    {
        if (value.ValueKind == JsonValueKind.Undefined) return new(RecordValueState.Missing, property.TypeFullName, null, null);
        var original = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        RecordValue Invalid() => new(RecordValueState.Invalid, property.TypeFullName, null, original);
        if (value.ValueKind == JsonValueKind.Undefined) return new(RecordValueState.Missing, property.TypeFullName, null, null);
        if (value.ValueKind == JsonValueKind.Null) return new(RecordValueState.Null, property.TypeFullName, null, null);
        var isString = value.ValueKind == JsonValueKind.String;
        var isNumber = value.ValueKind == JsonValueKind.Number;
        try
        {
            string? canonical = property.TypeFullName switch
            {
                "Edm.String" when isString => original,
                "Edm.Boolean" when value.ValueKind is JsonValueKind.True or JsonValueKind.False => value.GetBoolean() ? "true" : "false",
                "Edm.Byte" when isNumber => value.GetByte().ToString(Culture),
                "Edm.SByte" when isNumber => value.GetSByte().ToString(Culture),
                "Edm.Int16" when isNumber => value.GetInt16().ToString(Culture),
                "Edm.Int32" when isNumber => value.GetInt32().ToString(Culture),
                "Edm.Int64" when isNumber || isString => long.Parse(original!, NumberStyles.AllowLeadingSign, Culture).ToString(Culture),
                "Edm.Decimal" when isNumber || isString => DecimalValue(original!),
                "Edm.Double" when isNumber => double.IsFinite(value.GetDouble()) ? value.GetDouble().ToString("R", Culture) : null,
                "Edm.Single" when isNumber => float.IsFinite(value.GetSingle()) ? value.GetSingle().ToString("R", Culture) : null,
                "Edm.Guid" when isString => Guid.ParseExact(original!, "D").ToString("D"),
                "Edm.Date" when isString => DateOnly.ParseExact(original!, "yyyy-MM-dd", Culture).ToString("yyyy-MM-dd", Culture),
                "Edm.DateTimeOffset" when isString => DateTimeValue(original!),
                "Edm.TimeOfDay" when isString && Regex.IsMatch(original!, @"^\d{2}:\d{2}:\d{2}(\.\d{1,7})?$", RegexOptions.CultureInvariant) => TimeOnly.Parse(original!, Culture).Ticks.ToString(Culture),
                "Edm.Duration" when isString && Regex.IsMatch(original!, @"^-?P(?=\d|T)(?:\d+D)?(?:T(?:\d+H)?(?:\d+M)?(?:\d+(?:\.\d{1,7})?S)?)?$", RegexOptions.CultureInvariant) => XmlConvert.ToTimeSpan(original!).Ticks.ToString(Culture),
                _ when isString && property.EnumTypeFullName is not null => EnumValue(property, original!, schema),
                _ => null
            };
            return canonical is null ? Invalid() : new(RecordValueState.Present, property.TypeFullName, canonical, original);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or InvalidOperationException or ArgumentException)
        {
            return Invalid();
        }
    }

    public DataRecord ReadRecord(JsonElement row, PreparedEntityPlan plan, SchemaSnapshot schema, string company)
    {
        if (row.ValueKind != JsonValueKind.Object) throw new InvalidDataException("An OData record is not an object.");
        var properties = row.EnumerateObject().Select(property => property.Name).ToArray();
        if (properties.Distinct(StringComparer.Ordinal).Count() != properties.Length)
            throw new InvalidDataException("An OData record contains duplicate property names.");
        var values = new Dictionary<string, RecordValue>(StringComparer.Ordinal);
        foreach (var property in plan.Fields)
        {
            row.TryGetProperty(property.Name, out var value);
            values.Add(property.Name, Canonicalize(property, value, schema));
        }
        if (plan.Plan.Scope == DataScopeKind.Company)
        {
            if (!values.TryGetValue(plan.Plan.CompanyField!, out var companyValue) || companyValue.State != RecordValueState.Present
                || !string.Equals(companyValue.Canonical, company, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("OData returned a record outside the explicitly configured company scope.");
            values[plan.Plan.CompanyField!] = companyValue with { Canonical = "$company" };
        }
        var validKey = plan.Plan.KeyFields.All(name => values.TryGetValue(name, out var value) && value.State == RecordValueState.Present);
        var key = validKey ? JsonSerializer.Serialize(plan.Plan.KeyFields.Select(name => new
        {
            Field = name, values[name].EdmType, values[name].Canonical
        })) : null;
        return new DataRecord(values, key);
    }

    private static string DecimalValue(string text)
    {
        var match = Regex.Match(text, @"^(?<sign>-?)(?<whole>\d+)(?:\.(?<fraction>\d+))?(?:[eE](?<exp>[+-]?\d+))?$", RegexOptions.CultureInvariant);
        if (!match.Success || text.Length > 1024) throw new FormatException();
        var exponent = match.Groups["exp"].Success ? int.Parse(match.Groups["exp"].Value, Culture) : 0;
        if (Math.Abs((long)exponent) > 1024) throw new FormatException();
        var coefficient = BigInteger.Parse(match.Groups["sign"].Value + match.Groups["whole"].Value + match.Groups["fraction"].Value, Culture);
        var scale = match.Groups["fraction"].Length - exponent;
        if (coefficient.IsZero) return "0e0";
        while (coefficient % 10 == 0) { coefficient /= 10; scale--; }
        return coefficient.ToString(Culture) + "e" + (-scale).ToString(Culture);
    }
    private static string? DateTimeValue(string text)
    {
        if (!Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)) return null;
        return DateTimeOffset.Parse(text, Culture, DateTimeStyles.None).UtcTicks.ToString(Culture);
    }
    private static string? EnumValue(FieldSchema property, string text, SchemaSnapshot schema)
    {
        var type = schema.Enums.SingleOrDefault(candidate => candidate.FullName == property.EnumTypeFullName);
        if (type is null) return null;
        var names = text.Split(',', StringSplitOptions.TrimEntries);
        if (!type.IsFlags && names.Length != 1) return null;
        long combined = 0;
        foreach (var name in names)
        {
            var member = type.Members.SingleOrDefault(candidate => candidate.Name == name);
            if (member is null) return null;
            combined |= member.Value;
        }
        return combined.ToString(Culture);
    }
}