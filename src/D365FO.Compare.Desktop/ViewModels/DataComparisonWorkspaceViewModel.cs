using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using D365FO.Compare.Application.DataComparison;
using D365FO.Compare.Domain.Configuration;
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
    private readonly IDataComparisonSetupProvider? _setups;
    private readonly IEntityCountReader? _countReader;
    private readonly GeneralParametersViewModel? _generalParameters;
    private DataComparisonSetupSnapshot? _setupSnapshot;
    private readonly SemaphoreSlim _resetGate = new(1, 1);
    private IDataRunStore? _store;
    private CancellationTokenSource? _operation;
    private TaskCompletionSource _idle = CompletedSignal();
    private int _generation;
    private CountStatusFilter? _countStatusFilter;
    private bool _batchVisiblePlansRefresh;
    private bool _visiblePlansRefreshPending;
    private bool _closed;
    private readonly ObservableCollection<EntityDataPlanViewModel> _plans = [];
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool allEligible;
    [ObservableProperty] private string sourceCompany = "";
    [ObservableProperty] private string targetCompany = "";
    [ObservableProperty] private DataSetupStatus setupStatus = DataSetupStatus.NotLoaded;
    [ObservableProperty] private CompanySetupResult? sourceSetup;
    [ObservableProperty] private CompanySetupResult? targetSetup;
    [ObservableProperty] private LegalEntityOption? selectedSourceCompany;
    [ObservableProperty] private LegalEntityOption? selectedTargetCompany;
    [ObservableProperty] private bool manualSourceCompany;
    [ObservableProperty] private bool manualTargetCompany;
    [ObservableProperty] private bool showSelectedOnly;
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
    public bool ShowNonzeroCountsOnly
    {
        get => _countStatusFilter == CountStatusFilter.NonzeroOrUnknown;
        set => SetCountStatusFilter(CountStatusFilter.NonzeroOrUnknown, value);
    }
    public bool ShowZeroCountsOnly
    {
        get => _countStatusFilter == CountStatusFilter.ZeroOnBothSides;
        set => SetCountStatusFilter(CountStatusFilter.ZeroOnBothSides, value);
    }
    public bool ShowNotAvailableCountsOnly
    {
        get => _countStatusFilter == CountStatusFilter.NotAvailable;
        set => SetCountStatusFilter(CountStatusFilter.NotAvailable, value);
    }
    public bool ShowCountErrorsOnly
    {
        get => _countStatusFilter == CountStatusFilter.Error;
        set => SetCountStatusFilter(CountStatusFilter.Error, value);
    }
    public bool SelectedMode { get => !AllEligible; set { if (value) AllEligible = false; } }
    public bool IsSetupReady => SetupStatus is DataSetupStatus.Ready or DataSetupStatus.ReadyWithWarnings;
    public bool IsOperationalFormEnabled => IsSetupReady && !IsBusy;
    public bool CanLoadSetup => !IsBusy && _context.State.OnlineReady && _context.State.SourceSchema is not null && _context.State.TargetSchema is not null;
    public bool CanCountRows => IsOperationalFormEnabled && _context.State.OnlineReady && ActivePlans.Count > 0 && _countReader is not null;
    public bool CanCountSelectedRows => CanCountRows && ActivePlans.Any(plan => plan.IsSelected);
    public bool CanImportExcludedEntities => !IsBusy && _generalParameters is { IsBusy: false } && ActivePlans.Any(plan => plan.IsExcluded);
    public bool CanForceEligibility => !IsBusy && ActivePlans.Any(plan => !plan.Prepared.IsReady);
    public bool CanStart => IsOperationalFormEnabled && _context.State.OnlineReady && ActivePlans.Count > 0
        && (!ActivePlans.Any(plan => plan.Scope == DataScopeKind.Company && (AllEligible || plan.IsSelected))
            || ((ManualSourceCompany ? !string.IsNullOrWhiteSpace(SourceCompany) : SelectedSourceCompany is not null)
                && (ManualTargetCompany ? !string.IsNullOrWhiteSpace(TargetCompany) : SelectedTargetCompany is not null)));
    public IReadOnlyList<LegalEntityOption> SourceCompanies => SourceSetup is { IsComplete: true } ? SourceSetup.Companies : [];
    public IReadOnlyList<LegalEntityOption> TargetCompanies => TargetSetup is { IsComplete: true } ? TargetSetup.Companies : [];
    public bool SourceListMode => !ManualSourceCompany;
    public bool TargetListMode => !ManualTargetCompany;
    public bool CanUseManualSource => !IsBusy && _context.State.OnlineReady && _setupSnapshot is not null
        && !_setupSnapshot.Source.RequiresAuthentication && !_setupSnapshot.Target.RequiresAuthentication;
    public bool CanUseManualTarget => CanUseManualSource;
    public string SourceCompanyState => ManualSourceCompany ? "Source company: manual / UNVERIFIED"
        : SelectedSourceCompany is not null ? "Source company verified in loaded list"
        : string.IsNullOrWhiteSpace(SourceCompany) ? "Source company not selected" : "Saved Source code is not in the loaded list";
    public string TargetCompanyState => ManualTargetCompany ? "Target company: manual / UNVERIFIED"
        : SelectedTargetCompany is not null ? "Target company verified in loaded list"
        : string.IsNullOrWhiteSpace(TargetCompany) ? "Target company not selected" : "Saved Target code is not in the loaded list";
    public string SetupSummary => $"Setup: {SetupStatus} | Source: {SourceCompanies.Count} companies | Target: {TargetCompanies.Count} companies";
    public string SetupDetails => string.Join("\n", new[] { SourceSetup?.Diagnostic?.ToString(), TargetSetup?.Diagnostic?.ToString() }.Where(text => !string.IsNullOrWhiteSpace(text)));
    public string ConnectionState => _context.State.OnlineReady ? "Connections validated" : "Connections require validation and saved configuration";
    public string EnvironmentPair => $"{_context.State.Source?.DisplayName ?? "Source"} / {_context.State.Target?.DisplayName ?? "Target"}";
    private IReadOnlyList<EntityDataPlanViewModel> ActivePlans => _plans.Where(plan => !IsGloballyExcluded(plan.EntitySetName)).ToArray();
    public IReadOnlyList<EntityDataPlanViewModel> VisiblePlans => ActivePlans
        .Where(plan => plan.EntitySetName.Contains(EntitySearch, StringComparison.OrdinalIgnoreCase))
        .Where(plan => !ShowSelectedOnly || plan.IsSelected)
        .Where(HasMatchingCountStatus)
        .ToArray();
    public string SelectionSummary => $"{ActivePlans.Count(plan => plan.IsSelected):N0} selected / {ActivePlans.Count:N0} entities";
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
        IDataComparisonInteraction interaction, DataComparisonExporter exporter, IDataComparisonSetupProvider? setups = null,
        IEntityCountReader? countReader = null, GeneralParametersViewModel? generalParameters = null)
    {
        _context = context; _eligibility = eligibility; _runner = runner; _stores = stores;
        _profiles = profiles; _interaction = interaction; _exporter = exporter;
        _setups = setups;
        _countReader = countReader;
        _generalParameters = generalParameters;
        context.Changed += OnContextChanged;
        if (_generalParameters is not null) _generalParameters.PropertyChanged += OnGeneralParametersChanged;
        RebuildPlans();
    }

    private bool IsGloballyExcluded(string entitySetName) => _generalParameters?.SavedExcludedEntitySets
        .Contains(entitySetName, StringComparer.OrdinalIgnoreCase) == true;

    private void OnGeneralParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(GeneralParametersViewModel.SavedExcludedEntitySets)
            or nameof(GeneralParametersViewModel.IsBusy))
        {
            OnPropertyChanged(nameof(VisiblePlans));
            OnPropertyChanged(nameof(SelectionSummary));
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanCountRows));
            OnPropertyChanged(nameof(CanCountSelectedRows));
            OnPropertyChanged(nameof(CanImportExcludedEntities));
            OnPropertyChanged(nameof(CanForceEligibility));
            NotifySetup();
        }
    }

    private async void OnContextChanged(object? sender, EventArgs args)
    {
        _generation++;
        _operation?.Cancel();
        ResetSetup();
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
        OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(CanCountRows)); OnPropertyChanged(nameof(CanCountSelectedRows));
        OnPropertyChanged(nameof(CanImportExcludedEntities)); OnPropertyChanged(nameof(CanForceEligibility));
        OnPropertyChanged(nameof(ConnectionState)); OnPropertyChanged(nameof(EnvironmentPair));
        CountRowsCommand.NotifyCanExecuteChanged();
        CountSelectedRowsCommand.NotifyCanExecuteChanged();
        ImportExcludedEntitiesCommand.NotifyCanExecuteChanged();
        ForceEligibilityCommand.NotifyCanExecuteChanged();
    }
    private void OnPlanChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(EntityDataPlanViewModel.IsSelected))
        {
            OnPropertyChanged(nameof(SelectionSummary));
            OnPropertyChanged(nameof(VisiblePlans));
            OnPropertyChanged(nameof(CanCountSelectedRows));
            CountSelectedRowsCommand.NotifyCanExecuteChanged();
        }
        if (args.PropertyName == nameof(EntityDataPlanViewModel.IsExcluded))
        {
            OnPropertyChanged(nameof(CanImportExcludedEntities));
            ImportExcludedEntitiesCommand.NotifyCanExecuteChanged();
        }
        var countFilterChanged = (_countStatusFilter, args.PropertyName) switch
        {
            (CountStatusFilter.NonzeroOrUnknown, nameof(EntityDataPlanViewModel.HasRowsOrUnknown)) => true,
            (CountStatusFilter.ZeroOnBothSides, nameof(EntityDataPlanViewModel.HasZeroCounts)) => true,
            (CountStatusFilter.NotAvailable, nameof(EntityDataPlanViewModel.HasNotAvailableCount)) => true,
            (CountStatusFilter.Error, nameof(EntityDataPlanViewModel.HasCountError)) => true,
            _ => false
        };
        if (countFilterChanged)
        {
            if (_batchVisiblePlansRefresh) _visiblePlansRefreshPending = true;
            else OnPropertyChanged(nameof(VisiblePlans));
        }
        if (args.PropertyName == nameof(EntityDataPlanViewModel.Eligibility))
        {
            OnPropertyChanged(nameof(CanForceEligibility));
            ForceEligibilityCommand.NotifyCanExecuteChanged();
        }
        if (args.PropertyName is nameof(EntityDataPlanViewModel.IsSelected) or nameof(EntityDataPlanViewModel.Scope))
            OnPropertyChanged(nameof(CanStart));
    }
    [RelayCommand] private void ValidateConnections() => ConnectionValidationRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void ToggleSelectedOnly() => ShowSelectedOnly = !ShowSelectedOnly;
    [RelayCommand] private void SelectAll() { foreach (var plan in ActivePlans) plan.IsSelected = true; }
    [RelayCommand] private void ClearSelection() { foreach (var plan in ActivePlans) plan.IsSelected = false; }
    [RelayCommand] private void SelectVisible() { foreach (var plan in VisiblePlans) plan.IsSelected = true; }
    [RelayCommand] private void ExcludeVisible() { foreach (var plan in VisiblePlans) plan.IsExcluded = true; }
    [RelayCommand] private void ShowAllCountStatuses() => SetCountStatusFilter(null);
    [RelayCommand] private void ApplyCompanyScope()
    {
        var targets = BulkTargets();
        var applied = targets.Count(plan => plan.TrySetCompanyScope(BulkCompanyField));
        var skipped = targets.Length - applied;
        Status = skipped == 0 ? $"Company scope applied to {applied:N0} entities using {BulkCompanyField}."
            : $"Company scope applied to {applied:N0} entities; {skipped:N0} skipped because {BulkCompanyField} is not a scalar string field.";
    }
    [RelayCommand] private void ApplySharedScope()
    {
        var targets = BulkTargets();
        foreach (var plan in targets) plan.Scope = DataScopeKind.Shared;
        Status = $"Shared scope applied to {targets.Length:N0} entities.";
    }
    [RelayCommand(CanExecute = nameof(CanForceEligibility))]
    private void ForceEligibility()
    {
        var candidates = ActivePlans.Where(plan => !plan.Prepared.IsReady).ToArray();
        var blockedByKeys = candidates.Count(plan => !plan.HasMatchingODataKeys);
        var updated = candidates.Count(plan => plan.ForceExcludeSchemaDifferences());
        var remaining = ActivePlans.Count(plan => !plan.Prepared.IsReady);
        Status = $"Force Eligibility added exclusions to {updated:N0} entities; {blockedByKeys:N0} skipped because OData key fields differ or are missing; {remaining:N0} remain ineligible.";
    }
    [RelayCommand(CanExecute = nameof(CanImportExcludedEntities))]
    private Task ImportExcludedEntitiesAsync() => RunAsync(async _ =>
    {
        if (_generalParameters is null) throw new InvalidOperationException("General parameters are not available.");
        var flagged = ActivePlans.Where(plan => plan.IsExcluded).ToArray();
        if (flagged.Length == 0) throw new InvalidOperationException("Flag at least one entity in the Exclude column.");
        if (!await _generalParameters.AddExcludedEntitySetsAsync(flagged.Select(plan => plan.EntitySetName)))
        {
            Status = _generalParameters.Status;
            return;
        }
        foreach (var plan in flagged) plan.IsExcluded = false;
        Status = $"{flagged.Length:N0} entity exclusions added to General parameters and hidden from Data Scope.";
    });
    private EntityDataPlanViewModel[] BulkTargets()
    {
        var checkedPlans = ActivePlans.Where(plan => plan.IsSelected).ToArray();
        if (checkedPlans.Length > 0) return checkedPlans;
        return SelectedPlan is null || IsGloballyExcluded(SelectedPlan.EntitySetName) ? [] : [SelectedPlan];
    }
    [RelayCommand] private void Cancel() => _operation?.Cancel();

    [RelayCommand]
    private Task LoadSetupAsync() => RunAsync(async ct =>
    {
        if (!CanLoadSetupDuringOperation()) throw new InvalidOperationException("Validate connections and load both schemas before loading setup.");
        if (_setups is null) throw new InvalidOperationException("The setup provider is not registered.");
        var state = _context.State;
        var generation = _generation;
        _setupSnapshot = null;
        SourceSetup = null; TargetSetup = null;
        ManualSourceCompany = false; ManualTargetCompany = false;
        SelectedSourceCompany = null; SelectedTargetCompany = null;
        SetupStatus = DataSetupStatus.Loading;
        var active = true;
        var progress = new Progress<DataReadProgress>(update =>
        {
            if (active && generation == _generation && !ct.IsCancellationRequested)
                Progress = $"Setup / {update.Side}: {update.Stage} | {update.Rows} companies | page {update.Pages}";
        });
        try
        {
            await ReleaseStoreAsync();
            Manifest = null; Records = []; TotalRecords = 0;
            var result = await Task.Run(() => _setups.LoadAsync(state, progress, ct), ct);
            ct.ThrowIfCancellationRequested();
            if (generation != _generation) return;
            _setupSnapshot = result;
            SourceSetup = result.Source; TargetSetup = result.Target;
            ResolveCompanySelections();
            UpdateSetupStatus();
            Status = IsSetupReady ? "Setup loaded. Select companies and configure the comparison."
                : "Setup incomplete. Inspect setup diagnostics, retry, or explicitly select manual fallback if permitted.";
        }
        catch (OperationCanceledException) { if (generation == _generation) SetupStatus = DataSetupStatus.Canceled; throw; }
        catch { if (generation == _generation) SetupStatus = DataSetupStatus.Failed; throw; }
        finally { active = false; NotifySetup(); }
    });

    private bool CanLoadSetupDuringOperation() => _context.State.OnlineReady && _context.State.SourceSchema is not null && _context.State.TargetSchema is not null;
    [RelayCommand]
    private void ManualSource()
    {
        if (!CanUseManualSource) return;
        if (!ManualSourceCompany && !_interaction.Confirm("Use a manually entered Source company? It will be marked UNVERIFIED and does not grant FO permissions.")) return;
        ManualSourceCompany = !ManualSourceCompany;
    }
    [RelayCommand]
    private void ManualTarget()
    {
        if (!CanUseManualTarget) return;
        if (!ManualTargetCompany && !_interaction.Confirm("Use a manually entered Target company? It will be marked UNVERIFIED and does not grant FO permissions.")) return;
        ManualTargetCompany = !ManualTargetCompany;
    }
    private void UpdateSetupStatus()
    {
        if (_setupSnapshot is null) return;
        SetupStatus = _setupSnapshot.OtherRequirementsComplete && !_setupSnapshot.Source.RequiresAuthentication && !_setupSnapshot.Target.RequiresAuthentication
            && (_setupSnapshot.Source.IsComplete || ManualSourceCompany) && (_setupSnapshot.Target.IsComplete || ManualTargetCompany)
            ? ManualSourceCompany || ManualTargetCompany ? DataSetupStatus.ReadyWithWarnings : DataSetupStatus.Ready : DataSetupStatus.Failed;
        NotifySetup();
    }
    private void ResetSetup()
    {
        _setupSnapshot = null;
        SourceSetup = null; TargetSetup = null;
        ManualSourceCompany = false; ManualTargetCompany = false;
        SelectedSourceCompany = null; SelectedTargetCompany = null;
        SourceCompany = ""; TargetCompany = "";
        SetupStatus = DataSetupStatus.NotLoaded;
        NotifySetup();
    }
    private void ResolveCompanySelections()
    {
        SelectedSourceCompany = SourceCompanies.SingleOrDefault(company => string.Equals(company.Code, SourceCompany, StringComparison.OrdinalIgnoreCase));
        SelectedTargetCompany = TargetCompanies.SingleOrDefault(company => string.Equals(company.Code, TargetCompany, StringComparison.OrdinalIgnoreCase));
        NotifySetup();
    }
    private void NotifySetup()
    {
        foreach (var property in new[] { nameof(IsSetupReady), nameof(IsOperationalFormEnabled), nameof(CanLoadSetup), nameof(CanCountRows), nameof(CanStart),
            nameof(CanCountSelectedRows), nameof(CanImportExcludedEntities), nameof(CanForceEligibility), nameof(SourceCompanies), nameof(TargetCompanies), nameof(SourceListMode), nameof(TargetListMode), nameof(CanUseManualSource),
            nameof(CanUseManualTarget), nameof(SourceCompanyState), nameof(TargetCompanyState), nameof(SetupSummary), nameof(SetupDetails) })
            OnPropertyChanged(property);
            CountRowsCommand.NotifyCanExecuteChanged();
            CountSelectedRowsCommand.NotifyCanExecuteChanged();
            ImportExcludedEntitiesCommand.NotifyCanExecuteChanged();
            ClearRowCountsCommand.NotifyCanExecuteChanged();
            ForceEligibilityCommand.NotifyCanExecuteChanged();
    }
    private void RequireSetup()
    {
        if (!IsSetupReady) throw new InvalidOperationException("Press Load setup and complete the required setup before using the data comparison form.");
    }

    private DataComparisonRequest BuildRequest(bool onlyRunEntities)
    {
        RequireSetup();
        var state = _context.State;
        if (state.Source is null || state.Target is null || state.SourceSchema is null || state.TargetSchema is null)
            throw new InvalidOperationException("Load both schemas first.");
        var candidates = AllEligible && onlyRunEntities ? ActivePlans.Where(plan => plan.Prepared.IsReady).ToArray()
            : onlyRunEntities ? ActivePlans.Where(plan => plan.IsSelected).ToArray() : ActivePlans.ToArray();
        if (candidates.Length == 0) throw new InvalidOperationException("Select at least one configured entity.");
        var skipped = AllEligible && onlyRunEntities ? ActivePlans.Where(plan => !plan.Prepared.IsReady).Select(plan => plan.EntitySetName).ToArray() : [];
        var plans = candidates.Select(plan => plan.Build()).ToArray();
        if (onlyRunEntities && plans.Any(plan => plan.Scope == DataScopeKind.Company) && (string.IsNullOrWhiteSpace(SourceCompany) || string.IsNullOrWhiteSpace(TargetCompany)))
            throw new InvalidOperationException("Enter both company codes explicitly.");
        var limits = new DataReadLimits(MaxRows, MaxPages, 32L * 1024 * 1024, checked(MaxDiskMegabytes * 1024 * 1024), MaxEntities);
        return new(state.Source, state.Target, state.SourceSchema, state.TargetSchema, new(SourceCompany, TargetCompany, plans, limits,
            SkippedEntities: skipped, AllEligible: AllEligible, SelectedEntities: ActivePlans.Where(plan => plan.IsSelected).Select(plan => plan.EntitySetName).ToArray(),
            SourceCompanyUnverified: ManualSourceCompany, TargetCompanyUnverified: ManualTargetCompany));
    }

    [RelayCommand]
    private Task StartAsync() => RunAsync(async ct =>
    {
        if (!_context.State.OnlineReady) throw new InvalidOperationException("Revalidate connections and save configuration before reading data.");
        if (!CanStartDuringOperation()) throw new InvalidOperationException("Load setup and select valid company codes, or confirm manual mode explicitly.");
        var request = BuildRequest(true);
        if (request.Profile.Entities.Count > MaxEntities || MaxEntities < 1 || MaxRows < 1 || MaxPages < 1 || MaxDiskMegabytes < 1)
            throw new InvalidOperationException("The selected entity count or read limits are invalid.");
        if (!_interaction.Confirm($"Read business records from {EnvironmentPair}?\nCompany Source: {SourceCompany}\nCompany Target: {TargetCompany}\n" +
            $"Entities: {request.Profile.Entities.Count}; skipped: {request.Profile.SkippedEntities?.Count ?? 0}\n" +
            $"Rows per side/entity: {MaxRows:N0}; pages: {MaxPages}; temporary disk: {MaxDiskMegabytes} MiB\n" +
            $"Schemas captured: {request.SourceSchema.CapturedAtUtc:g} / {request.TargetSchema.CapturedAtUtc:g}\n" +
            $"Company validation: {SourceCompanyState}; {TargetCompanyState}\n" +
            "With no field checkboxes, the complete response is downloaded without $select, including fields excluded from comparison.\n" +
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

    [RelayCommand(CanExecute = nameof(CanCountRows))]
    private Task CountRowsAsync() => CountRowsCoreAsync(false);

    [RelayCommand(CanExecute = nameof(CanCountSelectedRows))]
    private Task CountSelectedRowsAsync() => CountRowsCoreAsync(true);

    private Task CountRowsCoreAsync(bool selectedOnly) => RunAsync(async ct =>
    {
        RequireSetup();
        var state = _context.State;
        if (!_context.State.OnlineReady || state.Source is null || state.Target is null || state.SourceSchema is null || state.TargetSchema is null)
            throw new InvalidOperationException("Revalidate connections and load both schemas before counting rows.");
        if (_countReader is null) throw new InvalidOperationException("The entity count reader is not registered.");

        var generation = _generation;
        var plans = ActivePlans.Where(viewModel => !selectedOnly || viewModel.IsSelected)
            .Select(viewModel => (ViewModel: viewModel, Plan: viewModel.Build())).ToArray();
        if (plans.Length == 0) throw new InvalidOperationException("Select at least one entity to count.");
        var work = plans.SelectMany(item => new[]
        {
            new CountWork(item.ViewModel.EntitySetName, item.Plan, true),
            new CountWork(item.ViewModel.EntitySetName, item.Plan, false)
        }).ToArray();
        var maxConcurrency = _generalParameters?.SavedMaxConcurrentCountRequests ?? GeneralPreferences.DefaultMaxConcurrentCountRequests;
        var results = new ConcurrentDictionary<(string Entity, bool Source), EntityCountReadResult>();
        var completed = 0;
        IProgress<EntityCountProgress> progress = new Progress<EntityCountProgress>(update =>
        {
            if (generation == _generation && !ct.IsCancellationRequested)
                Progress = $"Count rows / {update.Entity} / {update.Side}: {update.Completed} of {update.Total}";
        });
        await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct }, async (item, token) =>
        {
            var environment = item.IsSource ? state.Source : state.Target;
            var schema = item.IsSource ? state.SourceSchema : state.TargetSchema;
            var side = item.IsSource ? "Source" : "Target";
            EntityCountReadResult result;
            if (!schema.Entities.Any(entity => entity.EntitySetName == item.EntitySetName))
                result = new(null, Message: $"{item.EntitySetName} is not available in the {side} schema.");
            else
                result = await _countReader.CountAsync(environment, schema, item.Plan,
                    item.IsSource ? SourceCompany : TargetCompany, side, token).ConfigureAwait(false);
            results[(item.EntitySetName, item.IsSource)] = result;
            var done = Interlocked.Increment(ref completed);
            progress.Report(new(item.EntitySetName, side, done, work.Length));
        });
        ct.ThrowIfCancellationRequested();
        if (generation != _generation) return;
        var capturedAtUtc = DateTimeOffset.UtcNow;
        foreach (var item in plans)
        {
            item.ViewModel.SetSourceRows(results[(item.ViewModel.EntitySetName, true)], capturedAtUtc);
            item.ViewModel.SetTargetRows(results[(item.ViewModel.EntitySetName, false)], capturedAtUtc);
        }
        var successful = results.Values.Count(result => result.Count is not null);
        Status = $"{(selectedOnly ? "Selected " : "")}count rows completed: {successful:N0} of {work.Length:N0} side/entity requests returned a count.";
    });

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private void ClearRowCounts()
    {
        _batchVisiblePlansRefresh = true;
        try { ResetRowCounts(); }
        finally
        {
            _batchVisiblePlansRefresh = false;
            if (_visiblePlansRefreshPending)
            {
                _visiblePlansRefreshPending = false;
                OnPropertyChanged(nameof(VisiblePlans));
            }
        }
        Status = "Row counts cleared.";
    }

    [RelayCommand] private Task SaveProfileAsync() => RunAsync(async ct =>
    {
        var request = BuildRequest(false);
        var entityNames = request.Profile.Entities.Select(plan => plan.EntitySetName).ToHashSet(StringComparer.Ordinal);
        var rowCounts = _plans.Where(plan => entityNames.Contains(plan.EntitySetName))
            .Select(plan => plan.CaptureRowCounts()).OfType<EntityRowCountSnapshot>().ToArray();
        request = request with { Profile = request.Profile with { Version = 3, RowCounts = rowCounts } };
        var path = _interaction.SaveProfilePath(SuggestProfileFileName());
        if (path is null) { Status = "Profile save canceled."; return; }
        await Task.Run(() => _profiles.SaveAsync(request, path, ct), ct);
        Status = $"Profile saved to {path}. Configuration and {rowCounts.Length:N0} entity count snapshots were saved; no business record values were saved.";
    });
    [RelayCommand] private Task LoadProfileAsync() => RunAsync(async ct =>
    {
        RequireSetup();
        var state = _context.State;
        if (state.SourceSchema is null || state.TargetSchema is null) throw new InvalidOperationException("Load both schemas first.");
        var path = _interaction.LoadProfilePath();
        if (path is null) { Status = "Profile load canceled."; return; }
        var profile = await Task.Run(() => _profiles.LoadAsync(state.SourceSchema, state.TargetSchema, path, ct), ct)
            ?? throw new InvalidDataException("The selected profile is empty or incompatible.");
        SourceCompany = profile.SourceCompany; TargetCompany = profile.TargetCompany;
        ResolveCompanySelections();
        AllEligible = profile.AllEligible;
        MaxRows = profile.Limits.MaxRows; MaxPages = profile.Limits.MaxPages; MaxEntities = profile.Limits.MaxEntities; MaxDiskMegabytes = profile.Limits.MaxSpoolBytes / 1024 / 1024;
        var saved = profile.Entities.ToDictionary(plan => plan.EntitySetName, StringComparer.Ordinal);
        var rowCounts = (profile.RowCounts ?? []).ToDictionary(count => count.EntitySetName, StringComparer.Ordinal);
        foreach (var plan in _plans)
        {
            if (saved.TryGetValue(plan.EntitySetName, out var item)) plan.Restore(item);
            plan.IsSelected = profile.SelectedEntities?.Contains(plan.EntitySetName, StringComparer.Ordinal) == true;
            plan.RestoreRowCounts(rowCounts.GetValueOrDefault(plan.EntitySetName));
        }
        Status = $"Profile loaded from {path}. Configuration restored with {rowCounts.Count:N0} saved entity count snapshots; review scope and selection before starting.";
    });

    private string SuggestProfileFileName()
    {
        var state = _context.State;
        var source = state.Source?.DisplayName ?? state.SourceSchema?.EnvironmentName ?? "Source";
        var target = state.Target?.DisplayName ?? state.TargetSchema?.EnvironmentName ?? "Target";
        return $"DataComparison_{SanitizeFileName(source)}_to_{SanitizeFileName(target)}_{DateTime.Now:yyyyMMdd}.profile";
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = string.Concat(value.Select(character => invalid.Contains(character) || char.IsControl(character) ? '-' : character)).Trim(' ', '.');
        return string.IsNullOrWhiteSpace(sanitized) ? "Environment" : sanitized;
    }
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
        if (_generalParameters is not null) _generalParameters.PropertyChanged -= OnGeneralParametersChanged;
        _operation?.Cancel();
        await _idle.Task;
        await _resetGate.WaitAsync();
        try { await ReleaseStoreAsync(); } finally { _resetGate.Release(); }
    }
    private bool CanStartDuringOperation() => IsSetupReady && (!ActivePlans.Any(plan => plan.Scope == DataScopeKind.Company && (AllEligible || plan.IsSelected))
        || ((ManualSourceCompany ? !string.IsNullOrWhiteSpace(SourceCompany) : SelectedSourceCompany is not null)
            && (ManualTargetCompany ? !string.IsNullOrWhiteSpace(TargetCompany) : SelectedTargetCompany is not null)));
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(IsNotBusy)); NotifySetup(); }
    partial void OnAllEligibleChanged(bool value) { OnPropertyChanged(nameof(SelectedMode)); OnPropertyChanged(nameof(CanStart)); }
    partial void OnSetupStatusChanged(DataSetupStatus value) => NotifySetup();
    partial void OnSourceSetupChanged(CompanySetupResult? value) => NotifySetup();
    partial void OnTargetSetupChanged(CompanySetupResult? value) => NotifySetup();
    partial void OnSelectedSourceCompanyChanged(LegalEntityOption? value) { if (value is not null) SourceCompany = value.Code; NotifySetup(); }
    partial void OnSelectedTargetCompanyChanged(LegalEntityOption? value) { if (value is not null) TargetCompany = value.Code; NotifySetup(); }
    partial void OnSourceCompanyChanged(string value) { SelectedSourceCompany = SourceCompanies.SingleOrDefault(company => string.Equals(company.Code, value, StringComparison.OrdinalIgnoreCase)); NotifySetup(); }
    partial void OnTargetCompanyChanged(string value) { SelectedTargetCompany = TargetCompanies.SingleOrDefault(company => string.Equals(company.Code, value, StringComparison.OrdinalIgnoreCase)); NotifySetup(); }
    partial void OnManualSourceCompanyChanged(bool value) { UpdateSetupStatus(); NotifySetup(); }
    partial void OnManualTargetCompanyChanged(bool value) { UpdateSetupStatus(); NotifySetup(); }
    partial void OnEntitySearchChanged(string value) => OnPropertyChanged(nameof(VisiblePlans));
    partial void OnShowSelectedOnlyChanged(bool value) => OnPropertyChanged(nameof(VisiblePlans));
    partial void OnManifestChanged(DataRunManifest? value) => OnPropertyChanged(nameof(RunSummary));
    partial void OnSelectedRecordChanged(DataRecordResult? value) => OnPropertyChanged(nameof(SelectedFields));
    partial void OnRecordsChanged(IReadOnlyList<DataRecordResult> value) => OnPropertyChanged(nameof(PageSummary));
    partial void OnOffsetChanged(long value) => OnPropertyChanged(nameof(PageSummary));
    partial void OnTotalRecordsChanged(long value) => OnPropertyChanged(nameof(PageSummary));
    private void ResetRowCounts() { foreach (var plan in _plans) plan.ResetRows(); }
    private bool HasMatchingCountStatus(EntityDataPlanViewModel plan) => _countStatusFilter switch
    {
        CountStatusFilter.NonzeroOrUnknown => plan.HasRowsOrUnknown,
        CountStatusFilter.ZeroOnBothSides => plan.HasZeroCounts,
        CountStatusFilter.NotAvailable => plan.HasNotAvailableCount,
        CountStatusFilter.Error => plan.HasCountError,
        _ => true
    };
    private void SetCountStatusFilter(CountStatusFilter filter, bool enabled) =>
        SetCountStatusFilter(enabled ? filter : _countStatusFilter == filter ? null : _countStatusFilter);
    private void SetCountStatusFilter(CountStatusFilter? filter)
    {
        if (_countStatusFilter == filter) return;
        _countStatusFilter = filter;
        OnPropertyChanged(nameof(ShowNonzeroCountsOnly));
        OnPropertyChanged(nameof(ShowZeroCountsOnly));
        OnPropertyChanged(nameof(ShowNotAvailableCountsOnly));
        OnPropertyChanged(nameof(ShowCountErrorsOnly));
        OnPropertyChanged(nameof(VisiblePlans));
    }
    partial void OnSelectedEntityResultChanged(DataEntityRunResult? value) { Offset = 0; if (!IsBusy) LoadPageCommand.Execute(null); }
    partial void OnRecordFilterChanged(string value) { Offset = 0; if (!IsBusy) LoadPageCommand.Execute(null); }
    private static TaskCompletionSource CompletedSignal() { var source = new TaskCompletionSource(); source.SetResult(); return source; }
    private enum CountStatusFilter { NonzeroOrUnknown, ZeroOnBothSides, NotAvailable, Error }
    private sealed record CountWork(string EntitySetName, EntityDataPlan Plan, bool IsSource);
}