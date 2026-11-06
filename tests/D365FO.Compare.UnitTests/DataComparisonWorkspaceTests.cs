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
    internal static DataComparisonWorkspaceViewModel Create(ActiveComparisonContext context, Interaction? interaction = null) => new(context, new(),
        new DataComparisonRunner(new DataComparisonRunnerTests.SyntheticReader(), new(), new()), new SqliteDataRunStoreFactory(), new Profiles(),
        interaction ?? new Interaction(), new DataComparisonExporter());

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
    internal sealed class Interaction : IDataComparisonInteraction
    {
        public bool Accepted { get; init; } = true;
        public int Confirmations { get; private set; }
        public bool Confirm(string message) { Confirmations++; return Accepted; }
        public string? ExportPath(bool json) => null;
    }
    private sealed class Profiles : IDataComparisonProfileRepository
    {
        public Task SaveAsync(DataComparisonRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<DataComparisonProfile?> LoadAsync(SchemaSnapshot source, SchemaSnapshot target, CancellationToken cancellationToken) => Task.FromResult<DataComparisonProfile?>(null);
    }
}