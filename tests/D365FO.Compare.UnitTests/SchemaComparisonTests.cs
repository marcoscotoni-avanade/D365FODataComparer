using D365FO.Compare.Application.Comparison;
using D365FO.Compare.Application.Metadata;
using D365FO.Compare.Domain.Metadata;

namespace D365FO.Compare.UnitTests;

public sealed class SchemaComparisonTests
{
    internal static SchemaSnapshot Snapshot()
    {
        var parsed = MetadataTests.Parse();
        return new SchemaSnapshot("tenant", "account", "environment", "Example", "https://source.example", DateTimeOffset.UtcNow,
            parsed.Entities, parsed.Enums, parsed.ComplexTypes, [], []);
    }

    [Fact]
    public void FingerprintIgnoresOrderAndEnvironmentButPreservesKeyOrder()
    {
        var source = Snapshot();
        var reordered = source with { EnvironmentOrigin = "https://target.example", CapturedAtUtc = source.CapturedAtUtc.AddHours(1),
            Entities = source.Entities.Reverse().Select(entity => entity with { Fields = entity.Fields.Reverse().ToArray() }).ToArray() };
        Assert.Equal(SchemaFingerprint.Compute(source), SchemaFingerprint.Compute(reordered));
        Assert.NotEqual(SchemaFingerprint.Compute(source), SchemaFingerprint.Compute(source with
        {
            Entities = [source.Entities[0] with { Key = source.Entities[0].Key.Reverse().ToArray() }]
        }));
    }

    [Fact]
    public void ComparisonDetectsRemovalsAndSelectiveScope()
    {
        var source = Snapshot();
        var target = source with { Entities = [source.Entities[0]] };
        var comparer = new SchemaComparisonService();
        Assert.Equal(EntityChangeKind.Removed, comparer.Compare(source, target).Entities[1].Kind);
        Assert.Single(comparer.Compare(source, target, ["OtherCustomers"]).Entities);
        Assert.Throws<ArgumentException>(() => comparer.Compare(source, target, []));
    }

    [Fact]
    public void EqualFingerprintsDoNotClaimCompleteEquivalenceForUnknownFacets()
    {
        var source = Snapshot();
        var result = new SchemaComparisonService().Compare(source, source);
        Assert.False(result.AreEquivalent);
        Assert.All(result.Entities, entity => Assert.Equal(EntityChangeKind.Inconclusive, entity.Kind));
        Assert.False(result.HasBreakingChanges);
    }

    [Fact]
    public void ComparisonDetectsNarrowingEnumChangesAndUnknownWithoutFalseBreaking()
    {
        var source = Snapshot();
        var target = source with
        {
            Entities = source.Entities.Select(entity => entity with
            {
                Fields = entity.Fields.Select(field => field.Name == "Account" ? field with { MaxLength = ObservedValue.Declared("10") }
                    : field.Name == "Company" ? field with { MaxLength = ObservedValue.Missing() } : field).ToArray()
            }).ToArray(),
            Enums = [source.Enums[0] with { Members = [new EnumMemberSchema("Open", 7)] }]
        };
        var entity = new SchemaComparisonService().Compare(source, target).Entities[0];
        Assert.Contains(entity.Differences, change => change.Member == "Account" && change.Severity == DifferenceSeverity.Breaking);
        Assert.Contains(entity.Differences, change => change.Property == "Example.Status.Open" && change.Severity == DifferenceSeverity.Breaking);
        Assert.Contains(entity.Differences, change => change.Member == "Company" && !change.IsConclusive && change.Severity == DifferenceSeverity.Informational);
    }

    [Fact]
    public void DecimalScaleIncreaseCanNarrowIntegerRange()
    {
        var source = Snapshot();
        var target = source with { Entities = source.Entities.Select(entity => entity with
        {
            Fields = entity.Fields.Select(field => field.Name == "Credit" ? field with { Scale = ObservedValue.Declared("4") } : field).ToArray()
        }).ToArray() };
        Assert.Contains(new SchemaComparisonService().Compare(source, target).Entities[0].Differences,
            change => change.Property == "Scale" && change.Severity == DifferenceSeverity.Breaking);
    }
}