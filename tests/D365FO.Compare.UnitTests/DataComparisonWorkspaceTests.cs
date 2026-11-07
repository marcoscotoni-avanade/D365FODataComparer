using D365FO.Compare.Application.Configuration;
using D365FO.Compare.Application.DataComparison;
using D365FO.Compare.Desktop.ViewModels;
using D365FO.Compare.Domain.Configuration;
using D365FO.Compare.Domain.DataComparison;
using D365FO.Compare.Domain.Environments;
using D365FO.Compare.Domain.Metadata;
using D365FO.Compare.Infrastructure.Export;
using D365FO.Compare.Infrastructure.ODataData;
using D365FO.Compare.Infrastructure.Persistence;

namespace D365FO.Compare.UnitTests;

public sealed class DataComparisonWorkspaceTests
{
    internal static DataComparisonWorkspaceViewModel Create(ActiveComparisonContext context, Interaction? interaction = null, IDataComparisonSetupProvider? setups = null,
        IEntityCountReader? countReader = null, GeneralParametersViewModel? generalParameters = null) => new(context, new(),
        new DataComparisonRunner(new DataComparisonRunnerTests.SyntheticReader(), new(), new()), new SqliteDataRunStoreFactory(), new Profiles(),
        interaction ?? new Interaction(), new DataComparisonExporter(), setups ?? new SetupProvider(), countReader, generalParameters);

    internal static ActiveComparisonContext Context()
    {
        var request = DataComparisonRunnerTests.Request;
        var context = new ActiveComparisonContext();
        context.Update(new(request.Source, request.Target, request.SourceSchema, request.TargetSchema, true));
        return context;
    }

    [Fact]
    public async Task DataRunRequiresExplicitConfirmationAndShowsPagedResults()
    {
        var interaction = new Interaction();
        var workspace = Create(Context(), interaction);
        try
        {
            workspace.SourceCompany = "IT"; workspace.TargetCompany = "US";
            workspace.SelectedPlan!.Scope = DataScopeKind.Company;
            workspace.SelectedPlan.CompanyField = "Company";
            workspace.SelectedPlan.IsSelected = true;
            await workspace.LoadSetupCommand.ExecuteAsync(null);
            await workspace.StartCommand.ExecuteAsync(null);
            Assert.Equal(1, interaction.Confirmations);
            Assert.NotNull(workspace.Manifest);
            Assert.Equal(4, workspace.TotalRecords);
            Assert.Equal(4, workspace.Records.Count);
            workspace.RecordFilter = "Different";
            await workspace.LoadPageCommand.ExecutionTask!;
            Assert.Single(workspace.Records);
            Assert.Equal(DataRecordStatus.Different, workspace.SelectedRecord!.Status);
            await workspace.ClearResultsCommand.ExecuteAsync(null);
            Assert.Null(workspace.Manifest);
        }
        finally { await workspace.CloseAsync(); }
    }
    [Fact]
    public async Task RefusingConfirmationMakesNoRunAndRestoredContextRequiresValidation()
    {
        var context = Context();
        var workspace = Create(context, new Interaction { Accepted = false });
        try
        {
            workspace.SelectedPlan!.Scope = DataScopeKind.Shared;
            workspace.SelectedPlan.IsSelected = true;
            await workspace.LoadSetupCommand.ExecuteAsync(null);
            await workspace.StartCommand.ExecuteAsync(null);
            Assert.Null(workspace.Manifest);
            context.Update(context.State with { OnlineReady = false });
            Assert.False(workspace.CanStart);
        }
        finally { await workspace.CloseAsync(); }
    }
    [Fact]
    public async Task EmptyBusinessOverrideKeepsDefaultODataKeyValidation()
    {
        var workspace = Create(Context());
        try
        {
            Assert.Equal("", workspace.SelectedPlan!.KeyFieldsText);
            Assert.Equal("Company, Account", workspace.SelectedPlan.DefaultKey);
            Assert.False(workspace.SelectedPlan.Prepared.IsReady);
            workspace.SelectedPlan.Scope = DataScopeKind.Shared;
            Assert.True(workspace.SelectedPlan.Prepared.IsReady);
        }
        finally { await workspace.CloseAsync(); }
    }
    [Fact]
    public async Task SelectedOnlyToggleFiltersEntityGridAndCombinesWithSearch()
    {
        var workspace = Create(Context());
        try
        {
            var allPlans = workspace.VisiblePlans;
            Assert.True(allPlans.Count > 1);
            workspace.EntitySearch = "not-an-entity-name";
            workspace.SelectAllCommand.Execute(null);
            Assert.All(allPlans, plan => Assert.True(plan.IsSelected));
            workspace.ClearSelectionCommand.Execute(null);
            Assert.All(allPlans, plan => Assert.False(plan.IsSelected));
            workspace.EntitySearch = "";

            var selected = allPlans[0];
            selected.IsSelected = true;

            workspace.ToggleSelectedOnlyCommand.Execute(null);
            Assert.Same(selected, Assert.Single(workspace.VisiblePlans));

            var another = allPlans[1];
            another.IsSelected = true;
            Assert.Equal(2, workspace.VisiblePlans.Count);
            another.IsSelected = false;
            Assert.Same(selected, Assert.Single(workspace.VisiblePlans));

            workspace.EntitySearch = "not-an-entity-name";
            Assert.Empty(workspace.VisiblePlans);
            workspace.EntitySearch = selected.EntitySetName;
            Assert.Same(selected, Assert.Single(workspace.VisiblePlans));

            workspace.EntitySearch = "";
            workspace.ToggleSelectedOnlyCommand.Execute(null);
            Assert.Equal(allPlans.Count, workspace.VisiblePlans.Count);
            Assert.True(selected.IsSelected);
        }
        finally { await workspace.CloseAsync(); }
    }

    [Fact]
    public async Task CountRowsIncludesFilteredOutEntitiesAndHonorsSavedConcurrency()
    {
        var reader = new CountingReader();
        var preferences = new GeneralParametersViewModel(new TestPreferencesRepository(new GeneralPreferences(1)));
        await preferences.InitializeAsync();
        var workspace = Create(Context(), countReader: reader, generalParameters: preferences);
        try
        {
            var allPlans = workspace.VisiblePlans;
            Assert.True(allPlans.Count > 1);
            Assert.False(workspace.CanCountRows);
            await workspace.LoadSetupCommand.ExecuteAsync(null);
            Assert.True(workspace.CanCountRows);
            allPlans[0].IsSelected = true;
            workspace.ShowSelectedOnly = true;
            workspace.EntitySearch = "not-an-entity-name";
            Assert.Empty(workspace.VisiblePlans);

            await workspace.CountRowsCommand.ExecuteAsync(null);

            Assert.Equal(allPlans.Count * 2, reader.Calls);
            Assert.Equal(1, reader.MaximumConcurrency);
            Assert.All(allPlans, plan =>
            {
                Assert.Equal("4", plan.SourceRows);
                Assert.Equal("6", plan.TargetRows);
            });
            Assert.Contains("completed", workspace.Status, StringComparison.OrdinalIgnoreCase);
            allPlans[0].Scope = DataScopeKind.Shared;
            Assert.Equal("4", allPlans[0].SourceRows);
            Assert.Equal("6", allPlans[0].TargetRows);
            workspace.ClearRowCountsCommand.Execute(null);
            Assert.All(allPlans, plan =>
            {
                Assert.Equal("—", plan.SourceRows);
                Assert.Equal("—", plan.TargetRows);
            });
        }
        finally { await workspace.CloseAsync(); }
    }

    [Fact]
    public async Task SelectedRowCountOnlyUpdatesSelectedEntities()
    {
        var reader = new CountingReader();
        var workspace = Create(Context(), countReader: reader);
        try
        {
            var plans = workspace.VisiblePlans;
            await workspace.LoadSetupCommand.ExecuteAsync(null);
            Assert.False(workspace.CanCountSelectedRows);
            var selected = plans[0];
            selected.IsSelected = true;
            Assert.True(workspace.CanCountSelectedRows);

            await workspace.CountSelectedRowsCommand.ExecuteAsync(null);

            Assert.Equal(2, reader.Calls);
            Assert.Equal("4", selected.SourceRows);
            Assert.Equal("6", selected.TargetRows);
            Assert.All(plans.Skip(1), plan =>
            {
                Assert.Equal("—", plan.SourceRows);
                Assert.Equal("—", plan.TargetRows);
            });

            selected.Scope = DataScopeKind.Shared;
            Assert.Equal("4", selected.SourceRows);
            Assert.Equal("6", selected.TargetRows);
            reader.Offset = 10;
            await workspace.CountSelectedRowsCommand.ExecuteAsync(null);
            Assert.Equal(4, reader.Calls);
            Assert.Equal("14", selected.SourceRows);
            Assert.Equal("16", selected.TargetRows);
        }
        finally { await workspace.CloseAsync(); }
    }

    [Fact]
    public void ForceEligibilityExcludesFieldsUniqueToEitherSchemaAndPreservesExistingExclusions()
    {
        var source = SchemaComparisonTests.Snapshot();
        var target = source with
        {
            Entities = source.Entities.Select(entity => entity.EntitySetName == "Customers"
                ? entity with
                {
                    Fields = entity.Fields.Where(field => field.Name != "Credit")
                        .Append(entity.Fields.Single(field => field.Name == "Credit") with { Name = "TargetOnly" }).ToArray()
                }
                : entity).ToArray()
        };
        var plan = new EntityDataPlanViewModel("Customers", source, target, new())
        {
            Scope = DataScopeKind.Shared,
            ExcludedFieldsText = "Status"
        };

        Assert.False(plan.Prepared.IsReady);
        Assert.True(plan.ForceExcludeSchemaDifferences());
        Assert.Equal(["Status", "Credit", "TargetOnly"], plan.Build().ExcludedFields);
        Assert.True(plan.Prepared.IsReady);
    }

    [Fact]
    public async Task ForceEligibilityCommandProcessesEveryIneligibleEntityWithMatchingKeys()
    {
        var context = Context();
        var state = context.State;
        var targetSchema = state.TargetSchema!;
        var target = targetSchema with
        {
            Entities = targetSchema.Entities.Select(entity => entity.EntitySetName == "Customers"
                ? entity with { Fields = entity.Fields.Where(field => field.Name != "Credit").ToArray() }
                : entity).ToArray()
        };
        context.Update(state with { TargetSchema = target });
        var workspace = Create(context);
        try
        {
            var customers = workspace.VisiblePlans.Single(plan => plan.EntitySetName == "Customers");
            customers.Scope = DataScopeKind.Shared;
            Assert.False(customers.Prepared.IsReady);
            Assert.True(workspace.CanForceEligibility);

            workspace.ForceEligibilityCommand.Execute(null);

            Assert.True(customers.Prepared.IsReady);
            Assert.Contains("Credit", customers.Build().ExcludedFields);
            Assert.Contains("1 entities", workspace.Status);
        }
        finally { await workspace.CloseAsync(); }
    }

    [Fact]
    public async Task SetCompanyUsesActiveRowAndRejectsUnavailableCompanyField()
    {
        var workspace = Create(Context());
        try
        {
            var plan = workspace.VisiblePlans.First(item => item.CompanyFields.Contains("Company", StringComparer.Ordinal));
            workspace.SelectedPlan = plan;
            workspace.BulkCompanyField = "Company";
            workspace.ApplyCompanyScopeCommand.Execute(null);
            Assert.Equal(DataScopeKind.Company, plan.Scope);
            Assert.Equal("Company", plan.CompanyField);
            Assert.Contains("applied to 1 entities", workspace.Status, StringComparison.OrdinalIgnoreCase);

            plan.Scope = DataScopeKind.Shared;
            workspace.BulkCompanyField = "NotACompanyField";
            workspace.ApplyCompanyScopeCommand.Execute(null);
            Assert.Equal(DataScopeKind.Shared, plan.Scope);
            Assert.Contains("skipped", workspace.Status, StringComparison.OrdinalIgnoreCase);
        }
        finally { await workspace.CloseAsync(); }
    }

    [Fact]
    public void FieldCheckboxCommandsAndRestoreKeepFullModeAndRequiredFields()
    {
        var snapshot = SchemaComparisonTests.Snapshot();
        var plan = new EntityDataPlanViewModel("Customers", snapshot, snapshot, new());
        plan.Scope = DataScopeKind.Company;
        plan.CompanyField = "Company";
        Assert.All(plan.AvailableFields, item => Assert.False(item.IsSelected));
        Assert.Equal("Key / Company", plan.AvailableFields.Single(item => item.Name == "Company").Required);
        plan.SelectAllFieldsCommand.Execute(null);
        Assert.All(plan.Build().AvailableFields!, item => Assert.True(item.IsSelected));
        plan.ClearSelectedFieldsCommand.Execute(null);
        Assert.All(plan.Build().AvailableFields!, item => Assert.False(item.IsSelected));
        var saved = plan.Build();
        plan.SelectAllFieldsCommand.Execute(null);
        plan.Restore(saved);
        Assert.All(plan.AvailableFields, item => Assert.False(item.IsSelected));
        plan.AvailableFields.Single(item => item.Name == "Credit").IsSelected = true;
        Assert.DoesNotContain(plan.Prepared.Fields, property => property.Name == "Status");
        Assert.Contains(plan.Prepared.Fields, property => property.Name == "Account");
        plan.KeyFieldsText = "Account";
        Assert.Equal("Company", plan.AvailableFields.Single(item => item.Name == "Company").Required);
    }

    [Fact]
    public async Task SetupFreezesFormUntilLoadedAndDoesNotInventProfileCompany()
    {
        var context = Context();
        var workspace = Create(context);
        try
        {
            Assert.False(workspace.IsOperationalFormEnabled);
            Assert.False(workspace.CanStart);
            Assert.True(workspace.CanLoadSetup);
            Assert.Empty(workspace.SourceCompanies);
            workspace.SourceCompany = "missing-code";
            workspace.TargetCompany = "US";
            await workspace.LoadSetupCommand.ExecuteAsync(null);
            Assert.True(workspace.IsSetupReady);
            Assert.True(workspace.IsOperationalFormEnabled);
            Assert.Null(workspace.SelectedSourceCompany);
            Assert.Equal("missing-code", workspace.SourceCompany);
            Assert.Equal("US", workspace.SelectedTargetCompany!.Code);
            Assert.Equal("Target USA", workspace.SelectedTargetCompany.Name);
            workspace.SelectedPlan!.IsSelected = true;
            workspace.SelectedPlan.Scope = DataScopeKind.Company;
            workspace.SelectedPlan.CompanyField = "Company";
            Assert.False(workspace.CanStart);
            workspace.SelectedSourceCompany = workspace.SourceCompanies.Single(company => company.Code == "IT");
            Assert.Equal("IT", workspace.SourceCompany);
            Assert.True(workspace.CanStart);
            context.Update(context.State with { OnlineReady = false });
            Assert.False(workspace.IsSetupReady);
            Assert.Empty(workspace.SourceCompanies);
            Assert.False(workspace.CanStart);
        }
        finally { await workspace.CloseAsync(); }
    }

    [Fact]
    public async Task ManualFallbackRequiresExplicitChoiceAndCannotBypassOtherSetupRequirements()
    {
        var failed = new DataComparisonSetupSnapshot(
            new([], false, DateTimeOffset.UtcNow, new("Source", "Load setup", null, "IOException", "HTTP 403", 403)),
            new([new("US", "Target USA")], true, DateTimeOffset.UtcNow));
        var workspace = Create(Context(), setups: new FixedSetup(failed));
        try
        {
            await workspace.LoadSetupCommand.ExecuteAsync(null);
            Assert.False(workspace.IsOperationalFormEnabled);
            Assert.True(workspace.CanUseManualSource);
            workspace.ManualSourceCommand.Execute(null);
            Assert.Equal(DataSetupStatus.ReadyWithWarnings, workspace.SetupStatus);
            Assert.True(workspace.IsOperationalFormEnabled);
            Assert.Contains("UNVERIFIED", workspace.SourceCompanyState);
            workspace.SourceCompany = "IT"; workspace.TargetCompany = "US";
            workspace.SelectedPlan!.IsSelected = true;
            workspace.SelectedPlan.Scope = DataScopeKind.Company;
            workspace.SelectedPlan.CompanyField = "Company";
            await workspace.StartCommand.ExecuteAsync(null);
            Assert.True(workspace.Manifest!.Request.Profile.SourceCompanyUnverified);
            Assert.False(workspace.Manifest.Request.Profile.TargetCompanyUnverified);
        }
        finally { await workspace.CloseAsync(); }
        workspace = Create(Context(), setups: new FixedSetup(failed with { AdditionalSteps = [new("Future required setup", true, false)] }));
        try
        {
            await workspace.LoadSetupCommand.ExecuteAsync(null);
            workspace.ManualSourceCommand.Execute(null);
            Assert.False(workspace.IsSetupReady);
        }
        finally { await workspace.CloseAsync(); }
        workspace = Create(Context(), setups: new FixedSetup(failed with { Source = failed.Source with { RequiresAuthentication = true } }));
        try
        {
            await workspace.LoadSetupCommand.ExecuteAsync(null);
            Assert.False(workspace.CanUseManualSource);
            workspace.ManualSourceCommand.Execute(null);
            Assert.False(workspace.IsSetupReady);
        }
        finally { await workspace.CloseAsync(); }
    }

    [Fact]
    public async Task CancelSetupLeavesFormFrozen()
    {
        var waiting = new WaitingSetup();
        var workspace = Create(Context(), setups: waiting);
        try
        {
            var load = workspace.LoadSetupCommand.ExecuteAsync(null);
            await waiting.Started.Task;
            Assert.True(workspace.IsBusy);
            Assert.False(workspace.IsOperationalFormEnabled);
            workspace.CancelCommand.Execute(null);
            await load;
            Assert.Equal(DataSetupStatus.Canceled, workspace.SetupStatus);
            Assert.False(workspace.IsSetupReady);
            Assert.Empty(workspace.SourceCompanies);
        }
        finally { await workspace.CloseAsync(); }
    }

    private sealed class FixedSetup(DataComparisonSetupSnapshot result) : IDataComparisonSetupProvider
    {
        public Task<DataComparisonSetupSnapshot> LoadAsync(ComparisonContextState context, IProgress<DataReadProgress>? progress, CancellationToken cancellationToken) => Task.FromResult(result);
    }
    private sealed class TestPreferencesRepository(GeneralPreferences preferences) : IGeneralPreferencesRepository
    {
        public Task<GeneralPreferences> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(preferences);
        public Task SaveAsync(GeneralPreferences value, CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class CountingReader : IEntityCountReader
    {
        private int _active;
        private int _maximumConcurrency;
        private int _calls;
        public long Offset { get; set; }
        public int Calls => Volatile.Read(ref _calls);
        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public async Task<EntityCountReadResult> CountAsync(EnvironmentConnection environment, SchemaSnapshot schema, EntityDataPlan plan,
            string company, string side, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var active = Interlocked.Increment(ref _active);
            int current;
            while (active > (current = Volatile.Read(ref _maximumConcurrency))
                && Interlocked.CompareExchange(ref _maximumConcurrency, active, current) != current) { }
            try
            {
                await Task.Delay(20, cancellationToken);
                return new((side == "Source" ? 4 : 6) + Offset);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
    private sealed class WaitingSetup : IDataComparisonSetupProvider
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<DataComparisonSetupSnapshot> LoadAsync(ComparisonContextState context, IProgress<DataReadProgress>? progress, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }
    internal sealed class Interaction : IDataComparisonInteraction
    {
        public bool Accepted { get; init; } = true;
        public int Confirmations { get; private set; }
        public bool Confirm(string message) { Confirmations++; return Accepted; }
        public string? ExportPath(bool json) => null;
    }
    internal sealed class SetupProvider : IDataComparisonSetupProvider
    {
        public Task<DataComparisonSetupSnapshot> LoadAsync(ComparisonContextState context, IProgress<DataReadProgress>? progress, CancellationToken cancellationToken) => Task.FromResult(
            new DataComparisonSetupSnapshot(new([new("IT", "Source Italy"), new("US", "Source USA")], true, DateTimeOffset.UtcNow),
                new([new("US", "Target USA"), new("IT", "Target Italy")], true, DateTimeOffset.UtcNow)));
    }
    private sealed class Profiles : IDataComparisonProfileRepository
    {
        public Task SaveAsync(DataComparisonRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<DataComparisonProfile?> LoadAsync(SchemaSnapshot source, SchemaSnapshot target, CancellationToken cancellationToken) => Task.FromResult<DataComparisonProfile?>(null);
    }
}