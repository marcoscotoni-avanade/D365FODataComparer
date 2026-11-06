using D365FO.Compare.Application.DataComparison;
using D365FO.Compare.Desktop.ViewModels;
using D365FO.Compare.Domain.DataComparison;
using D365FO.Compare.Domain.Metadata;
using D365FO.Compare.Infrastructure.Export;
using D365FO.Compare.Infrastructure.ODataData;
using D365FO.Compare.Infrastructure.Persistence;

namespace D365FO.Compare.UnitTests;

public sealed class DataComparisonWorkspaceTests
{
    internal static DataComparisonWorkspaceViewModel Create(ActiveComparisonContext context, Interaction? interaction = null, IDataComparisonSetupProvider? setups = null) => new(context, new(),
        new DataComparisonRunner(new DataComparisonRunnerTests.SyntheticReader(), new(), new()), new SqliteDataRunStoreFactory(), new Profiles(),
        interaction ?? new Interaction(), new DataComparisonExporter(), setups ?? new SetupProvider());

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