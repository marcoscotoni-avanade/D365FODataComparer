using System.Runtime.CompilerServices;
using System.Text.Json;
using D365FO.Compare.Application.DataComparison;
using D365FO.Compare.Domain.DataComparison;
using D365FO.Compare.Domain.Environments;
using D365FO.Compare.Domain.Metadata;
using D365FO.Compare.Infrastructure.Export;
using D365FO.Compare.Infrastructure.ODataData;
using D365FO.Compare.Infrastructure.Persistence;

namespace D365FO.Compare.UnitTests;

public sealed class DataComparisonRunnerTests
{
    internal static DataComparisonRequest Request => new(ODataEntityDataReaderTests.Environment,
        ODataEntityDataReaderTests.Environment with { FinOpsUrl = new Uri("https://target.example"), ProviderEnvironmentId = "target" },
        SchemaComparisonTests.Snapshot(), SchemaComparisonTests.Snapshot() with { EnvironmentOrigin = "https://target.example", EnvironmentId = "target" },
        new("IT", "US", [DataComparisonTests.Plan], new()));

    [Fact]
    public async Task RunnerMatchesCompleteAcquisitionsAndExportIncludesScope()
    {
        await using var store = new SqliteDataRunStore(Guid.NewGuid(), 16 * 1024 * 1024);
        var runner = new DataComparisonRunner(new SyntheticReader(), new(), new());
        var manifest = await runner.RunAsync(Request, store, null, default);
        var entity = Assert.Single(manifest.Entities);
        Assert.Equal(DataEntityStatus.Completed, entity.Status);
        Assert.Equal(1, entity.Different);
        Assert.Equal(1, entity.OnlySource);
        Assert.Equal(1, entity.OnlyTarget);
        Assert.Equal(1, entity.Inconclusive);
        Assert.False(manifest.AreEqual);
        Assert.False(manifest.IsComplete);
        Assert.Equal(4, await store.CountResultsAsync("Customers", null, default));
        var path = Path.Combine(Path.GetTempPath(), "data-report-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await new DataComparisonExporter().ExportAsync(manifest, store, path, true, false, default);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.False(json.RootElement.GetProperty("manifest").GetProperty("IsComplete").GetBoolean());
            Assert.Equal("IT", json.RootElement.GetProperty("manifest").GetProperty("Profile").GetProperty("SourceCompany").GetString());
            Assert.Equal(4, json.RootElement.GetProperty("records").GetArrayLength());
            Assert.False(json.RootElement.GetProperty("manifest").TryGetProperty("Request", out _));
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task FailedTargetDoesNotProduceFalseMissingRecords()
    {
        await using var store = new SqliteDataRunStore(Guid.NewGuid(), 16 * 1024 * 1024);
        var runner = new DataComparisonRunner(new SyntheticReader { FailTarget = true }, new(), new());
        var manifest = await runner.RunAsync(Request, store, null, default);
        Assert.Equal(DataEntityStatus.Failed, manifest.Entities[0].Status);
        Assert.Equal("Target", manifest.Entities[0].Diagnostic!.Side);
        Assert.Equal("Reading data", manifest.Entities[0].Diagnostic!.Stage);
        Assert.Equal(1, manifest.Entities[0].Diagnostic!.Page);
        Assert.Equal("IOException", manifest.Entities[0].Diagnostic!.ExceptionType);
        Assert.Equal("Simulated incomplete target.", manifest.Entities[0].Diagnostic!.Reason);
        Assert.Contains("Side: Target", manifest.Entities[0].DiagnosticDetails);
        Assert.False(manifest.AreEqual);
        Assert.Equal(0, await store.CountResultsAsync("Customers", null, default));
    }
    [Fact]
    public void CsvProtectsFormulaStringsButPreservesTrustedTypedNegativeNumbers()
    {
        Assert.Equal("\"'=1+1\",\"'@SUM(A1)\",\"'-2\"", DataComparisonExporter.Csv(["=1+1", "@SUM(A1)", "-2"]));
        Assert.Equal("\"-2\"", DataComparisonExporter.Csv(["-2"], new HashSet<int> { 0 }));
        Assert.Equal("\"a,\"\"b\"\"\"", DataComparisonExporter.Csv(["a,\"b\""]));
    }

    [Fact]
    public async Task ProfileRoundTripsModeSelectionAndIsolatesAccount()
    {
        var directory = Path.Combine(Path.GetTempPath(), "data-profile-tests-" + Guid.NewGuid().ToString("N"));
        var repository = new JsonDataComparisonProfileRepository(directory);
        var request = Request with { Profile = Request.Profile with { AllEligible = true, SelectedEntities = ["Customers"] } };
        try
        {
            await repository.SaveAsync(request, default);
            var profile = await repository.LoadAsync(request.SourceSchema, request.TargetSchema, default);
            Assert.True(profile!.AllEligible);
            Assert.Equal("Customers", Assert.Single(profile.SelectedEntities!));
            Assert.Null(await repository.LoadAsync(request.SourceSchema with { AccountId = "other" }, request.TargetSchema, default));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task ProfileVersionTwoPreservesFieldCatalogAndEmptyFlagsAndMigratesLegacyExclusions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "field-profile-tests-" + Guid.NewGuid().ToString("N"));
        var repository = new JsonDataComparisonProfileRepository(directory);
        var fields = Request.SourceSchema.Entities.Single(entity => entity.EntitySetName == "Customers").Fields;
        var selections = fields.Select(property => new AvailableFieldSelection(property, false)).ToArray();
        var request = Request with { Profile = Request.Profile with { Entities = [DataComparisonTests.Plan with { AvailableFields = selections }] } };
        try
        {
            await repository.SaveAsync(request, default);
            var profile = await repository.LoadAsync(request.SourceSchema, request.TargetSchema, default);
            Assert.Equal(2, profile!.Version);
            var catalog = Assert.Single(profile.Entities).AvailableFields!;
            Assert.Equal(fields.Count, catalog.Count);
            Assert.All(catalog, item => Assert.False(item.IsSelected));
            Assert.Equal(fields[0].TypeFullName, catalog[0].Field.TypeFullName);
            Assert.Equal(fields[0].Nullable, catalog[0].Field.Nullable);
            var selected = request with { Profile = request.Profile with { Entities = [request.Profile.Entities[0] with
            {
                AvailableFields = selections.Select(item => item with { IsSelected = item.Field.Name == "Credit" }).ToArray()
            }] } };
            await repository.SaveAsync(selected, default);
            Assert.Equal("Credit", Assert.Single((await repository.LoadAsync(request.SourceSchema, request.TargetSchema, default))!.Entities[0].AvailableFields!, item => item.IsSelected).Field.Name);

            var legacy = request with { Profile = request.Profile with { Version = 1, Entities = [DataComparisonTests.Plan with { ExcludedFields = ["Credit"] }] } };
            await repository.SaveAsync(legacy, default);
            var migrated = await repository.LoadAsync(request.SourceSchema, request.TargetSchema, default);
            Assert.Equal(2, migrated!.Version);
            Assert.False(migrated.Entities[0].AvailableFields!.Single(item => item.Field.Name == "Credit").IsSelected);
            Assert.True(migrated.Entities[0].AvailableFields!.Single(item => item.Field.Name == "Account").IsSelected);
            await repository.SaveAsync(legacy with { Profile = legacy.Profile with { Entities = [DataComparisonTests.Plan] } }, default);
            Assert.All((await repository.LoadAsync(request.SourceSchema, request.TargetSchema, default))!.Entities[0].AvailableFields!, item => Assert.False(item.IsSelected));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    internal sealed class SyntheticReader : IEntityDataReader
    {
        public bool FailTarget { get; init; }
        public async IAsyncEnumerable<IReadOnlyList<DataRecord>> ReadPagesAsync(EnvironmentConnection environment, SchemaSnapshot schema,
            PreparedEntityPlan plan, string company, DataReadLimits limits, IProgress<DataReadProgress>? progress, string side,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (side == "Target" && FailTarget) throw new IOException("Simulated incomplete target.");
            yield return side == "Source"
                ? [DataRunStoreTests.Record("changed", "before"), DataRunStoreTests.Record("source-only", "source"), DataRunStoreTests.Record("duplicate", "one"), DataRunStoreTests.Record("duplicate", "two")]
                : [DataRunStoreTests.Record("changed", "after"), DataRunStoreTests.Record("target-only", "target"), DataRunStoreTests.Record("duplicate", "other")];
        }
    }
}