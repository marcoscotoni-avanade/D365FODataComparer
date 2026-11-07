using System.Text.Json;
using D365FO.Compare.Application.DataComparison;
using D365FO.Compare.Domain.DataComparison;
using D365FO.Compare.Domain.Metadata;
using D365FO.Compare.Infrastructure.ODataData;

namespace D365FO.Compare.UnitTests;

public sealed class DataComparisonTests
{
    internal static EntityDataPlan Plan => new("Customers", DataScopeKind.Company, "Company", [], [], []);
    internal static PreparedEntityPlan Prepared => new EntityDataEligibilityService().Prepare(SchemaComparisonTests.Snapshot(), SchemaComparisonTests.Snapshot(), Plan);
    internal static RecordValue Value(string type, string json)
    {
        using var document = JsonDocument.Parse(json);
        var property = new FieldSchema("Value", type, false, ObservedValue.Missing(), ObservedValue.Missing(), ObservedValue.Missing(), ObservedValue.Missing(), null, []);
        return new RecordValueCanonicalizer().Canonicalize(property, document.RootElement, SchemaComparisonTests.Snapshot());
    }
    [Fact]
    public void EligibilityRequiresCompanyConfirmationAndCompatibleKeys()
    {
        Assert.True(Prepared.IsReady);
        Assert.Equal(["Company", "Account"], Prepared.Plan.KeyFields);
        var service = new EntityDataEligibilityService();
        Assert.False(service.Prepare(SchemaComparisonTests.Snapshot(), SchemaComparisonTests.Snapshot(), Plan with { Scope = DataScopeKind.NotConfigured }).IsReady);
        Assert.False(service.Prepare(SchemaComparisonTests.Snapshot(), SchemaComparisonTests.Snapshot(), Plan with { ExcludedFields = ["Account"] }).IsReady);
    }
    [Fact]
    public void ODataKeyNamesMustMatchEvenWithBusinessKeyOverrideButOrderDoesNotMatter()
    {
        var source = SchemaComparisonTests.Snapshot();
        var reorderedTarget = source with
        {
            Entities = source.Entities.Select(entity => entity.EntitySetName == "Customers"
                ? entity with { Key = entity.Key.Reverse().ToArray() } : entity).ToArray()
        };
        var changedTarget = source with
        {
            Entities = source.Entities.Select(entity => entity.EntitySetName == "Customers"
                ? entity with { Key = [new KeyFieldSchema("Credit", 0)] } : entity).ToArray()
        };
        var service = new EntityDataEligibilityService();

        Assert.True(service.Prepare(source, reorderedTarget, Plan).IsReady);
        var mismatched = service.Prepare(source, changedTarget, Plan with { KeyFields = ["Company", "Account"] });
        Assert.False(mismatched.IsReady);
        Assert.Contains(mismatched.Problems, problem => problem.Contains("OData key fields", StringComparison.Ordinal));
    }
    [Fact]
    public void CanonicalValuesKeepPrecisionAndDistinguishNullEmptyAndFalse()
    {
        Assert.Equal("9007199254740993", Value("Edm.Int64", "9007199254740993").Canonical);
        Assert.Equal(Value("Edm.Decimal", "1.000").Canonical, Value("Edm.Decimal", "\"1e0\"").Canonical);
        Assert.NotEqual(Value("Edm.Decimal", "1.00000000000000000000000000001").Canonical, Value("Edm.Decimal", "1").Canonical);
        Assert.Equal(RecordValueState.Null, Value("Edm.String", "null").State);
        Assert.Equal("", Value("Edm.String", "\"\"").Canonical);
        Assert.Equal("false", Value("Edm.Boolean", "false").Canonical);
        Assert.Equal(RecordValueState.Invalid, Value("Edm.Boolean", "0").State);
        Assert.Equal(RecordValueState.Invalid, Value("Edm.Int32", "\"1\"").State);
    }
    [Fact]
    public void DatesCompareInstantsWithoutInventingTimezoneOrTruncatingPrecision()
    {
        Assert.Equal(Value("Edm.DateTimeOffset", "\"2026-01-01T10:00:00Z\"").Canonical, Value("Edm.DateTimeOffset", "\"2026-01-01T11:00:00+01:00\"").Canonical);
        Assert.Equal(RecordValueState.Invalid, Value("Edm.DateTimeOffset", "\"2026-01-01T10:00:00\"").State);
        Assert.Equal(RecordValueState.Invalid, Value("Edm.DateTimeOffset", "\"2026-01-01T10:00:00.12345678Z\"").State);
    }
    [Fact]
    public void ExplicitCompanyPairNormalizesKeysButPreservesOriginalValues()
    {
        using var source = JsonDocument.Parse("""{"Company":"IT","Account":"A","Credit":1,"Status":"Open"}""");
        using var target = JsonDocument.Parse("""{"Company":"US","Account":"A","Credit":1,"Status":"Open"}""");
        var canonicalizer = new RecordValueCanonicalizer();
        var left = canonicalizer.ReadRecord(source.RootElement, Prepared, SchemaComparisonTests.Snapshot(), "IT");
        var right = canonicalizer.ReadRecord(target.RootElement, Prepared, SchemaComparisonTests.Snapshot(), "US");
        Assert.Equal(left.Key, right.Key);
        Assert.Equal("IT", left.Values["Company"].Original);
        Assert.Equal("US", right.Values["Company"].Original);
        Assert.Throws<InvalidDataException>(() => canonicalizer.ReadRecord(source.RootElement, Prepared, SchemaComparisonTests.Snapshot(), "OTHER"));
    }

    [Fact]
    public void MatchingDistinguishesDuplicatesInvalidKeysAndValueChanges()
    {
        var service = new DataComparisonService();
        var left = DataRunStoreTests.Record("key", "left");
        var right = DataRunStoreTests.Record("key", "right");
        Assert.Equal(DataRecordStatus.Different, service.Compare([left], [right]).Status);
        Assert.Equal(DataRecordStatus.Equal, service.Compare([left], [left]).Status);
        Assert.Equal(DataRecordStatus.OnlySource, service.Compare([left], []).Status);
        Assert.Equal(DataRecordStatus.DuplicateKey, service.Compare([left, left], [right]).Status);
        Assert.Equal(DataRecordStatus.InvalidKey, service.Compare([left with { Key = null }], []).Status);
        Assert.Equal(DataRecordStatus.Inconclusive, service.Compare([left], [right with { Key = "hash-collision" }]).Status);
    }

    [Fact]
    public void MissingValuesAndMissingKeysAreNotNullOrEqual()
    {
        using var document = JsonDocument.Parse("""{"Company":"IT","Account":"A"}""");
        var record = new RecordValueCanonicalizer().ReadRecord(document.RootElement, Prepared, SchemaComparisonTests.Snapshot(), "IT");
        Assert.Equal(RecordValueState.Missing, record.Values["Credit"].State);
        Assert.Equal(DataRecordStatus.Inconclusive, new DataComparisonService().Compare([record], [record]).Status);
        using var withoutKey = JsonDocument.Parse("""{"Company":"IT"}""");
        Assert.Null(new RecordValueCanonicalizer().ReadRecord(withoutKey.RootElement, Prepared, SchemaComparisonTests.Snapshot(), "IT").Key);
        Assert.Equal(RecordValueState.Invalid, Value("Edm.Duration", "\"P1M\"").State);
    }

    [Fact]
    public void FieldSelectionEmptyOmitsSelectAndExplicitSelectionAlwaysIncludesKeys()
    {
        var snapshot = SchemaComparisonTests.Snapshot();
        var service = new EntityDataEligibilityService();
        var builder = new ODataQueryBuilder(new());
        var full = service.Prepare(snapshot, snapshot, Plan);
        Assert.DoesNotContain("$select", builder.Build(new Uri("https://source.example"), full, snapshot, "IT").Query);
        Assert.Contains("cross-company=true", builder.Build(new Uri("https://source.example"), full, snapshot, "IT").Query);
        var selectedPlan = Plan with { AvailableFields = full.Fields.Select(property => new AvailableFieldSelection(property, property.Name == "Credit")).ToArray() };
        var selected = service.Prepare(snapshot, snapshot, selectedPlan);
        Assert.True(selected.IsReady);
        Assert.Equal(["Account", "Company", "Credit"], selected.Fields.Select(property => property.Name));
        Assert.Contains("$select=Account,Company,Credit", Uri.UnescapeDataString(builder.Build(new Uri("https://source.example"), selected, snapshot, "IT").Query));
        var shared = service.Prepare(snapshot, snapshot, Plan with { Scope = DataScopeKind.Shared, CompanyField = null });
        Assert.Equal("", builder.Build(new Uri("https://source.example"), shared, snapshot, "").Query);
    }

    [Fact]
    public void FieldSelectionFullWithExclusionsDoesNotPersistExcludedValues()
    {
        var snapshot = SchemaComparisonTests.Snapshot();
        var catalog = Prepared.Fields.Select(property => new AvailableFieldSelection(property, false)).ToArray();
        var plan = Plan with { AvailableFields = catalog, ExcludedFields = ["Credit"] };
        var prepared = new EntityDataEligibilityService().Prepare(snapshot, snapshot, plan);
        Assert.DoesNotContain("$select", new ODataQueryBuilder(new()).Build(new Uri("https://source.example"), prepared, snapshot, "IT").Query);
        using var row = JsonDocument.Parse("""{"Company":"IT","Account":"A","Credit":123,"Status":"Open"}""");
        var record = new RecordValueCanonicalizer().ReadRecord(row.RootElement, prepared, snapshot, "IT");
        Assert.False(record.Values.ContainsKey("Credit"));
        var allExcluded = plan with { AvailableFields = catalog.Select(item => item with { IsSelected = item.Field.Name == "Credit" }).ToArray() };
        Assert.False(new EntityDataEligibilityService().Prepare(snapshot, snapshot, allExcluded).IsReady);
    }

    [Fact]
    public void FieldSelectionLargeFullProjectionDoesNotCreateLongQuery()
    {
        var snapshot = SchemaComparisonTests.Snapshot();
        var entity = snapshot.Entities[0];
        var fields = entity.Fields.Concat(Enumerable.Range(0, 314).Select(index => entity.Fields[0] with { Name = "LongCustomerFieldName" + index })).ToArray();
        snapshot = snapshot with { Entities = [entity with { Fields = fields }] };
        var service = new EntityDataEligibilityService();
        var builder = new ODataQueryBuilder(new());
        var full = service.Prepare(snapshot, snapshot, Plan);
        Assert.True(builder.Build(new Uri("https://source.example"), full, snapshot, "IT").Query.Length < 200);
        var explicitAll = service.Prepare(snapshot, snapshot, Plan with { AvailableFields = fields.Select(property => new AvailableFieldSelection(property, true)).ToArray() });
        Assert.True(builder.Build(new Uri("https://source.example"), explicitAll, snapshot, "IT").Query.Length > 7000);
    }
}