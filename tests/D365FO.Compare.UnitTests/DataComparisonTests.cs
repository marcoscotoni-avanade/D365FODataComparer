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
}