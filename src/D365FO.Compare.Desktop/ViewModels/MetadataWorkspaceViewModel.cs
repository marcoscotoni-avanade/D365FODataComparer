using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using D365FO.Compare.Application.Comparison;
using D365FO.Compare.Application.DataComparison;
using D365FO.Compare.Application.Metadata;
using D365FO.Compare.Domain.Authentication;
using D365FO.Compare.Domain.Configuration;
using D365FO.Compare.Domain.Environments;
using D365FO.Compare.Domain.Metadata;

namespace D365FO.Compare.Desktop.ViewModels;

public partial class MetadataEnvironmentViewModel(string role) : ObservableObject
{
    public string Role { get; } = role;
    public EnvironmentConnection? Environment { get; private set; }
    [ObservableProperty] private SchemaSnapshot? snapshot;
    [ObservableProperty] private string status = "Save an environment configuration first.";
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private EntitySchema? selectedEntity;
    public string Title => Environment is null ? Role : $"{Role} / {Environment.DisplayName}";
    public string Origin => Environment?.FinOpsUrl.GetLeftPart(UriPartial.Authority) ?? "";
    public IReadOnlyList<EntitySchema> VisibleEntities => Snapshot?.Entities.Where(entity => string.IsNullOrWhiteSpace(SearchText)
        || entity.EntitySetName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
        || entity.FoEntityName?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) == true).ToArray() ?? [];
    public string Counts => Snapshot is null ? "No snapshot" : $"{VisibleEntities.Count:N0} / {Snapshot.Entities.Count:N0} OData entities | {Snapshot.Catalog.Count:N0} FO catalog entries";
    public string CapturedAt => Snapshot is null ? "" : $"Captured {Snapshot.CapturedAtUtc.ToLocalTime():g}";
    public string KeyDisplay => SelectedEntity is null ? "" : string.Join(" > ", SelectedEntity.Key.Select(key => key.Name));
    public string SelectedDetails => SelectedEntity is null ? "" : JsonSerializer.Serialize(new
    {
        SelectedEntity.EntityTypeFullName, SelectedEntity.Key, SelectedEntity.Correlation, SelectedEntity.Fields,
        Enums = Snapshot?.Enums.Where(type => SelectedEntity.Fields.Any(property => property.EnumTypeFullName == type.FullName)),
        ComplexTypes = Snapshot?.ComplexTypes.Where(type => SelectedEntity.Fields.Any(property => property.TypeFullName == type.FullName)),
        SelectedEntity.Navigations, SelectedEntity.Annotations
    }, new JsonSerializerOptions { WriteIndented = true });
    public string Diagnostics => Snapshot is null ? "" : string.Join(System.Environment.NewLine,
        Snapshot.Diagnostics.Select(diagnostic => $"{diagnostic.Subject}: {diagnostic.Message}"));
    public IReadOnlyList<CatalogEntry> CatalogOnly
    {
        get
        {
            if (Snapshot is null) return [];
            var published = Snapshot.Entities.Select(entity => (entity.EntitySetName,
                entity.EntityTypeFullName[(entity.EntityTypeFullName.LastIndexOf('.') + 1)..])).ToHashSet();
            return Snapshot.Catalog.Where(entry => !published.Contains((entry.PublicCollectionName ?? "", entry.PublicEntityName ?? ""))).ToArray();
        }
    }

    public void Configure(EnvironmentConnection? environment)
    {
        Environment = environment;
        Snapshot = null;
        Status = environment is null ? "Save an environment configuration first." : "Ready to load metadata.";
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Origin));
    }
    partial void OnSearchTextChanged(string value) { OnPropertyChanged(nameof(VisibleEntities)); OnPropertyChanged(nameof(Counts)); }
    partial void OnSelectedEntityChanged(EntitySchema? value) { OnPropertyChanged(nameof(KeyDisplay)); OnPropertyChanged(nameof(SelectedDetails)); }
    partial void OnSnapshotChanged(SchemaSnapshot? value)
    {
        SelectedEntity = null;
        OnPropertyChanged(nameof(VisibleEntities));
        OnPropertyChanged(nameof(Counts));
        OnPropertyChanged(nameof(CapturedAt));
        OnPropertyChanged(nameof(Diagnostics));
        OnPropertyChanged(nameof(CatalogOnly));
        OnPropertyChanged(nameof(SelectedDetails));
    }
}

public partial class EntitySelectionItem(string name, bool inSource, bool inTarget) : ObservableObject
{
    public string EntitySetName { get; } = name;
    public bool InSource { get; } = inSource;
    public bool InTarget { get; } = inTarget;
    [ObservableProperty] private bool isSelected;
}

public partial class MetadataWorkspaceViewModel(
    IEntityMetadataProvider provider,
    ISchemaSnapshotRepository repository,
    SchemaComparisonService comparer,
    ActiveComparisonContext? comparisonContext = null) : ObservableObject
{
    private CancellationTokenSource? _operation;
    private string? _accountId;
    private bool _canRefresh;
    private int _generation;
    private readonly ObservableCollection<EntitySelectionItem> _selection = [];
    public event EventHandler? ConnectionValidationRequested;
    public MetadataEnvironmentViewModel Source { get; } = new("SOURCE");
    public MetadataEnvironmentViewModel Target { get; } = new("TARGET");
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool selectedOnly;
    [ObservableProperty] private string selectionSearch = "";
    [ObservableProperty] private string resultSearch = "";
    [ObservableProperty] private string severityFilter = "All severities";
    [ObservableProperty] private string status = "Save a configuration to open the metadata workspace.";
    [ObservableProperty] private SchemaComparisonResult? result;
    [ObservableProperty] private EntityComparison? selectedResult;
    public bool IsNotBusy => !IsBusy;
    public bool CompareAll { get => !SelectedOnly; set { if (value) SelectedOnly = false; } }
    public bool CanRefresh => !IsBusy && _canRefresh && _accountId is not null;
    public bool CanValidateConnections => !IsBusy && !_canRefresh && _accountId is not null
        && Source.Environment is not null && Target.Environment is not null;
    public string ConnectionState => _accountId is null ? "No active configuration"
        : _canRefresh ? "Connections validated" : "Connections require revalidation";
    public bool CanCompare => !IsBusy && Source.Snapshot is not null && Target.Snapshot is not null;
    public IReadOnlyList<string> SeverityFilters { get; } = ["All severities", "Breaking", "ConstraintChange", "Additive", "Informational", "None"];
    public IReadOnlyList<EntitySelectionItem> FilteredSelection => _selection.Where(item => item.EntitySetName.Contains(SelectionSearch, StringComparison.OrdinalIgnoreCase)).ToArray();
    public string SelectionCount => $"{_selection.Count(item => item.IsSelected):N0} selected / {_selection.Count:N0}";
    public IReadOnlyList<EntityComparison> VisibleResults => Result?.Entities.Where(entity =>
        entity.EntitySetName.Contains(ResultSearch, StringComparison.OrdinalIgnoreCase)
        && (SeverityFilter == "All severities" || entity.Severity.ToString() == SeverityFilter)).ToArray() ?? [];
    public string ResultSummary => Result is null ? "No comparison run." :
        $"{Result.Scope}: {Result.Entities.Count:N0} entities | {Result.Entities.Count(entity => entity.Kind == EntityChangeKind.Added)} added | " +
        $"{Result.Entities.Count(entity => entity.Kind == EntityChangeKind.Removed)} removed | {Result.Entities.Count(entity => entity.Kind == EntityChangeKind.Changed)} changed | " +
        $"{Result.Entities.Count(entity => !entity.IsComplete)} incomplete | Equivalent: {Result.AreEquivalent}";

    public async Task ConfigureAsync(AppConfiguration configuration, UserSession session, bool canRefresh, CancellationToken cancellationToken)
    {
        Reset();
        if (configuration.AuthenticationContext?.HomeAccountId != session.HomeAccountId
            || !string.Equals(configuration.AuthenticationContext.TenantId, session.TenantId, StringComparison.OrdinalIgnoreCase)) return;
        _accountId = session.HomeAccountId;
        _canRefresh = canRefresh;
        Source.Configure(configuration.SourceEnvironment);
        Target.Configure(configuration.TargetEnvironment);
        var generation = _generation;
        foreach (var panel in new[] { Source, Target })
        {
            if (panel.Environment is null) continue;
            try
            {
                var cached = await repository.LoadAsync(panel.Environment, _accountId, cancellationToken);
                if (_generation != generation) return;
                panel.Snapshot = cached;
                panel.Status = cached is null ? "No cached snapshot." : "Cached snapshot. Refresh explicitly for current metadata.";
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
            {
                panel.Status = "Cache could not be loaded. Clear the cache and refresh.";
            }
        }
        RebuildSelection();
        Status = canRefresh ? "Ready. Metadata is read only when requested."
            : Source.Snapshot is null && Target.Snapshot is null
                ? "No cached schemas. Re-test both connections and save configuration before loading metadata."
                : "Cached schemas available. Re-test both connections and save configuration before refreshing.";
        NotifyAvailability();
    }

    public void Reset()
    {
        _generation++;
        _operation?.Cancel();
        _accountId = null;
        _canRefresh = false;
        Source.Configure(null);
        Target.Configure(null);
        Result = null;
        RebuildSelection();
        Status = "Save a configuration to open the metadata workspace.";
        NotifyAvailability();
    }

    [RelayCommand] private Task LoadBothAsync() => RunAsync(async ct =>
    {
        if (!CanRefreshDuringOperation()) return;
        await RefreshAsync(Source, ct);
        await RefreshAsync(Target, ct);
    });
    [RelayCommand] private Task RefreshSourceAsync() => RunAsync(ct => RefreshAsync(Source, ct));
    [RelayCommand] private Task RefreshTargetAsync() => RunAsync(ct => RefreshAsync(Target, ct));
    [RelayCommand] private void Cancel() => _operation?.Cancel();
    [RelayCommand(CanExecute = nameof(CanValidateConnections))]
    private void ValidateConnections() => ConnectionValidationRequested?.Invoke(this, EventArgs.Empty);

    private bool CanRefreshDuringOperation()
    {
        if (_canRefresh && _accountId is not null) return true;
        Status = "Re-test both connections and save configuration before loading metadata.";
        return false;
    }

    private async Task RefreshAsync(MetadataEnvironmentViewModel panel, CancellationToken cancellationToken)
    {
        if (!CanRefreshDuringOperation() || panel.Environment is null) return;
        Result = null;
        var generation = _generation;
        var clock = Stopwatch.StartNew();
        var progressActive = true;
        panel.Status = "Waiting for metadata...";
        var progress = new Progress<MetadataProgress>(update =>
        {
            if (!progressActive || generation != _generation || cancellationToken.IsCancellationRequested) return;
            panel.Status = $"{update.Stage} | {update.BytesReceived / 1024d / 1024d:N1} MB | {clock.Elapsed:mm\\:ss}";
        });
        try
        {
            var snapshot = await provider.CreateSnapshotAsync(panel.Environment, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _generation) return;
            await repository.SaveAsync(snapshot, cancellationToken);
            if (generation != _generation) return;
            panel.Snapshot = snapshot;
            panel.Status = $"Loaded both endpoints in {clock.Elapsed:mm\\:ss}. {snapshot.Diagnostics.Count:N0} mapping diagnostics.";
            RebuildSelection();
        }
        catch
        {
            panel.Status = panel.Snapshot is null ? "Acquisition incomplete. No snapshot published." : "Refresh failed/canceled. Previous snapshot retained; not updated.";
            throw;
        }
        finally { progressActive = false; }
    }

    [RelayCommand]
    private Task CompareAsync() => RunAsync(async ct =>
    {
        if (Source.Snapshot is not { } source || Target.Snapshot is not { } target)
            throw new InvalidOperationException("Load a complete snapshot for both environments first.");
        var selection = SelectedOnly ? _selection.Where(item => item.IsSelected).Select(item => item.EntitySetName).ToArray() : null;
        var generation = _generation;
        Status = "Comparing schemas...";
        var comparison = await Task.Run(() => comparer.Compare(source, target, selection, ct), ct);
        if (generation == _generation) Result = comparison;
    });

    [RelayCommand] private void SelectFiltered()
    {
        foreach (var item in FilteredSelection) item.IsSelected = true;
    }
    [RelayCommand] private void ClearSelection()
    {
        foreach (var item in _selection) item.IsSelected = false;
    }

    [RelayCommand]
    private Task ClearCacheAsync() => RunAsync(async ct =>
    {
        if (_accountId is null) return;
        foreach (var panel in new[] { Source, Target })
        {
            if (panel.Environment is null) continue;
            await repository.DeleteAsync(panel.Environment, _accountId, ct);
            panel.Snapshot = null;
            panel.Status = "Cache cleared.";
        }
        Result = null;
        RebuildSelection();
    });

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        IsBusy = true;
        try
        {
            await action(operation.Token);
            if (!operation.IsCancellationRequested) Status = Result is null ? "Ready." : ResultSummary;
        }
        catch (OperationCanceledException) { Status = "Operation canceled."; }
        catch (Exception exception)
        {
            Status = exception is IOException or InvalidDataException or TimeoutException or InvalidOperationException or ArgumentException
                ? exception.Message : "The metadata operation failed. Verify the signed-in account, FO permissions and connectivity.";
        }
        finally
        {
            _operation = null;
            IsBusy = false;
        }
    }

    private void RebuildSelection()
    {
        var selected = _selection.Where(item => item.IsSelected).Select(item => item.EntitySetName).ToHashSet(StringComparer.Ordinal);
        foreach (var item in _selection) item.PropertyChanged -= OnSelectionChanged;
        _selection.Clear();
        var sourceNames = Source.Snapshot?.Entities.Select(entity => entity.EntitySetName).ToHashSet(StringComparer.Ordinal) ?? [];
        var targetNames = Target.Snapshot?.Entities.Select(entity => entity.EntitySetName).ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var name in sourceNames.Union(targetNames, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var item = new EntitySelectionItem(name, sourceNames.Contains(name), targetNames.Contains(name)) { IsSelected = selected.Contains(name) };
            item.PropertyChanged += OnSelectionChanged;
            _selection.Add(item);
        }
        OnPropertyChanged(nameof(FilteredSelection));
        OnPropertyChanged(nameof(SelectionCount));
        NotifyAvailability();
    }
    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs args) => OnPropertyChanged(nameof(SelectionCount));
    private void NotifyAvailability()
    {
        comparisonContext?.Update(new(Source.Environment, Target.Environment, Source.Snapshot, Target.Snapshot, _canRefresh && !IsBusy && _accountId is not null));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanCompare));
        OnPropertyChanged(nameof(CanValidateConnections));
        OnPropertyChanged(nameof(ConnectionState));
        ValidateConnectionsCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(IsNotBusy)); NotifyAvailability(); }
    partial void OnSelectionSearchChanged(string value) => OnPropertyChanged(nameof(FilteredSelection));
    partial void OnSelectedOnlyChanged(bool value) => OnPropertyChanged(nameof(CompareAll));
    partial void OnResultSearchChanged(string value) => OnPropertyChanged(nameof(VisibleResults));
    partial void OnSeverityFilterChanged(string value) => OnPropertyChanged(nameof(VisibleResults));
    partial void OnResultChanged(SchemaComparisonResult? value)
    {
        SelectedResult = null;
        OnPropertyChanged(nameof(VisibleResults));
        OnPropertyChanged(nameof(ResultSummary));
    }
}