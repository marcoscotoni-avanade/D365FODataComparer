using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using D365FO.Compare.Application.DataComparison;
using D365FO.Compare.Domain.DataComparison;
using D365FO.Compare.Infrastructure.Export;

namespace D365FO.Compare.Desktop.ViewModels;

public sealed record DataFieldDetail(string Field, string? Source, string? Target, string SourceState, string TargetState, string? SourceCanonical, string? TargetCanonical);

public partial class DataComparisonWorkspaceViewModel : ObservableObject
{
    private readonly ActiveComparisonContext _context;
    private readonly EntityDataEligibilityService _eligibility;
    private readonly IDataComparisonRunner _runner;
    private readonly IDataRunStoreFactory _stores;
    private readonly IDataComparisonProfileRepository _profiles;
    private readonly IDataComparisonInteraction _interaction;
    private readonly DataComparisonExporter _exporter;
    private readonly SemaphoreSlim _resetGate = new(1, 1);
    private IDataRunStore? _store;
    private CancellationTokenSource? _operation;
    private TaskCompletionSource _idle = CompletedSignal();
    private int _generation;
    private bool _closed;
    private readonly ObservableCollection<EntityDataPlanViewModel> _plans = [];
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool allEligible;
    [ObservableProperty] private string sourceCompany = "";
    [ObservableProperty] private string targetCompany = "";
    [ObservableProperty] private string entitySearch = "";
    [ObservableProperty] private string bulkCompanyField = "dataAreaId";
    [ObservableProperty] private long maxRows = 100000;
    [ObservableProperty] private int maxPages = 1000;
    [ObservableProperty] private long maxDiskMegabytes = 2048;
    [ObservableProperty] private int maxEntities = 100;
    [ObservableProperty] private string status = "Load Source and Target schemas before configuring a data comparison.";
    [ObservableProperty] private string progress = "";
    [ObservableProperty] private EntityDataPlanViewModel? selectedPlan;
    [ObservableProperty] private DataRunManifest? manifest;
    [ObservableProperty] private DataEntityRunResult? selectedEntityResult;
    [ObservableProperty] private string recordFilter = "All statuses";
    [ObservableProperty] private long offset;
    [ObservableProperty] private long totalRecords;
    [ObservableProperty] private IReadOnlyList<DataRecordResult> records = [];
    [ObservableProperty] private DataRecordResult? selectedRecord;
    [ObservableProperty] private bool includeEqualInExport;
    public event EventHandler? ConnectionValidationRequested;
    public bool IsNotBusy => !IsBusy;
    public bool SelectedMode { get => !AllEligible; set { if (value) AllEligible = false; } }
    public bool CanStart => !IsBusy && _context.State.OnlineReady && _context.State.SourceSchema is not null && _context.State.TargetSchema is not null && _plans.Count > 0;
    public string ConnectionState => _context.State.OnlineReady ? "Connections validated" : "Connections require validation and saved configuration";
    public string EnvironmentPair => $"{_context.State.Source?.DisplayName ?? "Source"} / {_context.State.Target?.DisplayName ?? "Target"}";
    public IReadOnlyList<EntityDataPlanViewModel> VisiblePlans => _plans.Where(plan => plan.EntitySetName.Contains(EntitySearch, StringComparison.OrdinalIgnoreCase)).ToArray();
    public string SelectionSummary => $"{_plans.Count(plan => plan.IsSelected):N0} selected / {_plans.Count:N0} entities";
    public IReadOnlyList<string> RecordFilters { get; } = ["All statuses", .. Enum.GetNames<DataRecordStatus>()];
    public string PageSummary => $"{Offset + (Records.Count == 0 ? 0 : 1):N0} - {Offset + Records.Count:N0} / {TotalRecords:N0}";
    public string RunSummary => Manifest is null ? "No data run." : $"Complete: {Manifest.IsComplete} | Equal: {Manifest.AreEqual} | " +
        $"{Manifest.Entities.Count} entities | {Manifest.Request.Profile.SkippedEntities?.Count ?? 0} blocked/skipped | " +
        $"{Manifest.StartedUtc.ToLocalTime():g} - {Manifest.FinishedUtc?.ToLocalTime():g}";
    public IReadOnlyList<DataFieldDetail> SelectedFields
    {
        get
        {
            if (SelectedRecord is null) return [];
            return (SelectedRecord.Source?.Keys ?? []).Union(SelectedRecord.Target?.Keys ?? [], StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(name =>
            {
                var before = SelectedRecord.Source?.GetValueOrDefault(name);
                var after = SelectedRecord.Target?.GetValueOrDefault(name);
                return new DataFieldDetail(name, before?.Original, after?.Original, before?.State.ToString() ?? "Absent record",
                    after?.State.ToString() ?? "Absent record", before?.Canonical, after?.Canonical);
            }).ToArray();
        }
    }

    public DataComparisonWorkspaceViewModel(ActiveComparisonContext context, EntityDataEligibilityService eligibility,
        IDataComparisonRunner runner, IDataRunStoreFactory stores, IDataComparisonProfileRepository profiles,
        IDataComparisonInteraction interaction, DataComparisonExporter exporter)
    {
        _context = context; _eligibility = eligibility; _runner = runner; _stores = stores;
        _profiles = profiles; _interaction = interaction; _exporter = exporter;
        context.Changed += OnContextChanged;
        RebuildPlans();
    }

    private async void OnContextChanged(object? sender, EventArgs args)
    {
        _generation++;
        _operation?.Cancel();
        await _resetGate.WaitAsync();
        try
        {
            await _idle.Task;
            await ReleaseStoreAsync();
            if (_closed) return;
            Manifest = null; Records = []; SelectedRecord = null;
            RebuildPlans();
            Status = "Data scope changed. Configure and confirm a new data run.";
        }
        catch (IOException) { Status = "Temporary data cleanup failed. Close the app and check local disk access."; }
        finally { _resetGate.Release(); }
    }
    private void RebuildPlans()
    {
        foreach (var plan in _plans) plan.PropertyChanged -= OnPlanChanged;
        _plans.Clear();
        if (_context.State.SourceSchema is { } source && _context.State.TargetSchema is { } target)
            foreach (var name in source.Entities.Select(entity => entity.EntitySetName).Union(target.Entities.Select(entity => entity.EntitySetName), StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var plan = new EntityDataPlanViewModel(name, source, target, _eligibility);
                plan.PropertyChanged += OnPlanChanged;
                _plans.Add(plan);
            }
        SelectedPlan = _plans.FirstOrDefault();
        OnPropertyChanged(nameof(VisiblePlans)); OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(ConnectionState)); OnPropertyChanged(nameof(EnvironmentPair));
    }
    private void OnPlanChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(EntityDataPlanViewModel.IsSelected)) OnPropertyChanged(nameof(SelectionSummary));
    }
    [RelayCommand] private void ValidateConnections() => ConnectionValidationRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void SelectFiltered() { foreach (var plan in VisiblePlans) plan.IsSelected = true; }
    [RelayCommand] private void ClearSelection() { foreach (var plan in _plans) plan.IsSelected = false; }
    [RelayCommand] private void ApplyCompanyScope()
    {
        foreach (var plan in _plans.Where(plan => plan.IsSelected)) { plan.CompanyField = BulkCompanyField; plan.Scope = DataScopeKind.Company; }
    }
    [RelayCommand] private void ApplySharedScope() { foreach (var plan in _plans.Where(plan => plan.IsSelected)) plan.Scope = DataScopeKind.Shared; }
    [RelayCommand] private void Cancel() => _operation?.Cancel();

    private DataComparisonRequest BuildRequest(bool onlyRunEntities)
    {
        var state = _context.State;
        if (state.Source is null || state.Target is null || state.SourceSchema is null || state.TargetSchema is null)
            throw new InvalidOperationException("Load both schemas first.");
        var candidates = AllEligible && onlyRunEntities ? _plans.Where(plan => plan.Prepared.IsReady).ToArray()
            : onlyRunEntities ? _plans.Where(plan => plan.IsSelected).ToArray() : _plans.ToArray();
        if (candidates.Length == 0) throw new InvalidOperationException("Select at least one configured entity.");
        var skipped = AllEligible && onlyRunEntities ? _plans.Where(plan => !plan.Prepared.IsReady).Select(plan => plan.EntitySetName).ToArray() : [];
        var plans = candidates.Select(plan => plan.Build()).ToArray();
        if (onlyRunEntities && plans.Any(plan => plan.Scope == DataScopeKind.Company) && (string.IsNullOrWhiteSpace(SourceCompany) || string.IsNullOrWhiteSpace(TargetCompany)))
            throw new InvalidOperationException("Enter both company codes explicitly.");
        var limits = new DataReadLimits(MaxRows, MaxPages, 32L * 1024 * 1024, checked(MaxDiskMegabytes * 1024 * 1024), MaxEntities);
        return new(state.Source, state.Target, state.SourceSchema, state.TargetSchema, new(SourceCompany, TargetCompany, plans, limits,
            SkippedEntities: skipped, AllEligible: AllEligible, SelectedEntities: _plans.Where(plan => plan.IsSelected).Select(plan => plan.EntitySetName).ToArray()));
    }

    [RelayCommand]
    private Task StartAsync() => RunAsync(async ct =>
    {
        if (!_context.State.OnlineReady) throw new InvalidOperationException("Revalidate connections and save configuration before reading data.");
        var request = BuildRequest(true);
        if (request.Profile.Entities.Count > MaxEntities || MaxEntities < 1 || MaxRows < 1 || MaxPages < 1 || MaxDiskMegabytes < 1)
            throw new InvalidOperationException("The selected entity count or read limits are invalid.");
        if (!_interaction.Confirm($"Read business records from {EnvironmentPair}?\nCompany Source: {SourceCompany}\nCompany Target: {TargetCompany}\n" +
            $"Entities: {request.Profile.Entities.Count}; skipped: {request.Profile.SkippedEntities?.Count ?? 0}\n" +
            $"Rows per side/entity: {MaxRows:N0}; pages: {MaxPages}; temporary disk: {MaxDiskMegabytes} MiB\n" +
            $"Schemas captured: {request.SourceSchema.CapturedAtUtc:g} / {request.TargetSchema.CapturedAtUtc:g}\n" +
            "Matching keys, fields, exclusions and filters use the current profile. Records are read sequentially, not in a cross-environment transaction.")) return;
        await ReleaseStoreAsync();
        Manifest = null; Records = []; TotalRecords = 0;
        var store = await Task.Run(() => _stores.Create(Guid.NewGuid(), request.Profile.Limits.MaxSpoolBytes), ct);
        _store = store;
        var generation = _generation;
        var clock = Stopwatch.StartNew();
        var active = true;
        var progress = new Progress<DataReadProgress>(update =>
        {
            if (active && generation == _generation && !ct.IsCancellationRequested)
                Progress = $"{update.Entity} | {update.Side}: {update.Stage} | {update.Rows:N0} rows | {update.Pages} pages | {update.Bytes / 1024d / 1024d:N1} MiB | {clock.Elapsed:mm\\:ss}";
        });
        try
        {
            var manifest = await Task.Run(() => _runner.RunAsync(request, store, progress, ct), CancellationToken.None);
            if (generation != _generation) return;
            Manifest = manifest;
            SelectedEntityResult = manifest.Entities.FirstOrDefault(entity => entity.Status == DataEntityStatus.Completed) ?? manifest.Entities.FirstOrDefault();
            Offset = 0;
            if (!ct.IsCancellationRequested) await LoadPageCoreAsync(ct);
        }
        finally { active = false; }
        Status = RunSummary;
    });

    [RelayCommand] private Task SaveProfileAsync() => RunAsync(async ct =>
    {
        var request = BuildRequest(false);
        await Task.Run(() => _profiles.SaveAsync(request, ct), ct);
        Status = "Profile saved for this environment/account/schema pair. No record values were saved.";
    });
    [RelayCommand] private Task LoadProfileAsync() => RunAsync(async ct =>
    {
        var state = _context.State;
        if (state.SourceSchema is null || state.TargetSchema is null) throw new InvalidOperationException("Load both schemas first.");
        var profile = await Task.Run(() => _profiles.LoadAsync(state.SourceSchema, state.TargetSchema, ct), ct)
            ?? throw new InvalidOperationException("No profile exists for this account/environment/schema pair.");
        SourceCompany = profile.SourceCompany; TargetCompany = profile.TargetCompany;
        AllEligible = profile.AllEligible;
        MaxRows = profile.Limits.MaxRows; MaxPages = profile.Limits.MaxPages; MaxEntities = profile.Limits.MaxEntities; MaxDiskMegabytes = profile.Limits.MaxSpoolBytes / 1024 / 1024;
        var saved = profile.Entities.ToDictionary(plan => plan.EntitySetName, StringComparer.Ordinal);
        foreach (var plan in _plans)
        {
            if (saved.TryGetValue(plan.EntitySetName, out var item)) plan.Restore(item);
            plan.IsSelected = profile.SelectedEntities?.Contains(plan.EntitySetName, StringComparer.Ordinal) == true;
        }
        Status = "Profile loaded. Review scope and selection before starting.";
    });
    [RelayCommand] private Task LoadPageAsync() => RunAsync(LoadPageCoreAsync);
    [RelayCommand] private Task NextPageAsync() => RunAsync(async ct => { if (Offset + 100 < TotalRecords) Offset += 100; await LoadPageCoreAsync(ct); });
    [RelayCommand] private Task PreviousPageAsync() => RunAsync(async ct => { Offset = Math.Max(0, Offset - 100); await LoadPageCoreAsync(ct); });
    private async Task LoadPageCoreAsync(CancellationToken cancellationToken)
    {
        Records = []; SelectedRecord = null;
        if (_store is null || SelectedEntityResult is not { Status: DataEntityStatus.Completed } entity) { TotalRecords = 0; return; }
        var filter = Enum.TryParse<DataRecordStatus>(RecordFilter, out var status) ? status : (DataRecordStatus?)null;
        TotalRecords = await Task.Run(() => _store.CountResultsAsync(entity.EntitySetName, filter, cancellationToken), cancellationToken);
        Records = await Task.Run(() => _store.ReadResultsAsync(entity.EntitySetName, filter, Offset, 100, cancellationToken), cancellationToken);
        SelectedRecord = Records.FirstOrDefault();
        OnPropertyChanged(nameof(PageSummary));
    }
    [RelayCommand] private Task ExportJsonAsync() => ExportAsync(true);
    [RelayCommand] private Task ExportCsvAsync() => ExportAsync(false);
    private Task ExportAsync(bool json) => RunAsync(async ct =>
    {
        if (_store is null || Manifest is null) throw new InvalidOperationException("Run a data comparison before exporting.");
        if (!_interaction.Confirm("The export contains business values and keys. Store it only in an approved location.\n" +
            (Manifest.IsComplete ? "" : "This report is INCOMPLETE; errors and scope limitations will be included."))) return;
        var path = _interaction.ExportPath(json);
        if (path is null) return;
        await Task.Run(() => _exporter.ExportAsync(Manifest, _store, path, json, IncludeEqualInExport, ct), ct);
        Status = "Export completed.";
    });
    [RelayCommand] private Task ClearResultsAsync() => RunAsync(async _ =>
    {
        await ReleaseStoreAsync(); Manifest = null; Records = []; TotalRecords = 0; SelectedRecord = null;
        Status = "Temporary data and results cleared.";
    });
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy || _closed) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IsBusy = true;
        try { await action(operation.Token); }
        catch (OperationCanceledException) { Status = "Operation canceled. Incomplete acquisitions are not equivalent."; }
        catch (Exception exception)
        {
            Status = exception is InvalidOperationException or DataAuthorizationException or DataLimitException or InvalidDataException
                ? exception.Message : "The data operation failed. Verify entity access, profile, connectivity and local disk permissions.";
        }
        finally { _operation = null; IsBusy = false; _idle.TrySetResult(); }
    }
    private async Task ReleaseStoreAsync()
    {
        var store = Interlocked.Exchange(ref _store, null);
        if (store is not null) await Task.Run(async () => await store.DisposeAsync());
    }
    public async Task CloseAsync()
    {
        _closed = true;
        _context.Changed -= OnContextChanged;
        _operation?.Cancel();
        await _idle.Task;
        await _resetGate.WaitAsync();
        try { await ReleaseStoreAsync(); } finally { _resetGate.Release(); }
    }
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(IsNotBusy)); OnPropertyChanged(nameof(CanStart)); }
    partial void OnAllEligibleChanged(bool value) => OnPropertyChanged(nameof(SelectedMode));
    partial void OnEntitySearchChanged(string value) => OnPropertyChanged(nameof(VisiblePlans));
    partial void OnManifestChanged(DataRunManifest? value) => OnPropertyChanged(nameof(RunSummary));
    partial void OnSelectedRecordChanged(DataRecordResult? value) => OnPropertyChanged(nameof(SelectedFields));
    partial void OnRecordsChanged(IReadOnlyList<DataRecordResult> value) => OnPropertyChanged(nameof(PageSummary));
    partial void OnOffsetChanged(long value) => OnPropertyChanged(nameof(PageSummary));
    partial void OnTotalRecordsChanged(long value) => OnPropertyChanged(nameof(PageSummary));
    partial void OnSelectedEntityResultChanged(DataEntityRunResult? value) { Offset = 0; if (!IsBusy) LoadPageCommand.Execute(null); }
    partial void OnRecordFilterChanged(string value) { Offset = 0; if (!IsBusy) LoadPageCommand.Execute(null); }
    private static TaskCompletionSource CompletedSignal() { var source = new TaskCompletionSource(); source.SetResult(); return source; }
}