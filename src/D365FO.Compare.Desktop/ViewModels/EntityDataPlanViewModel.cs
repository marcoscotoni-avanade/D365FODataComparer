using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using D365FO.Compare.Application.DataComparison;
using D365FO.Compare.Domain.DataComparison;
using D365FO.Compare.Domain.Metadata;

namespace D365FO.Compare.Desktop.ViewModels;

public partial class DataFilterViewModel(IReadOnlyList<FieldSchema> fields) : ObservableObject
{
    public IReadOnlyList<string> FieldNames => fields.Select(property => property.Name).ToArray();
    public IReadOnlyList<string> Operators { get; } = ["eq", "ne", "gt", "ge", "lt", "le"];
    [ObservableProperty] private string fieldName = "";
    [ObservableProperty] private string filterOperator = "eq";
    [ObservableProperty] private string value = "";
    public DataFilter Build()
    {
        var type = fields.SingleOrDefault(property => property.Name == FieldName);
        var quote = type?.TypeFullName is "Edm.String" or "Edm.Guid" or "Edm.Date" or "Edm.DateTimeOffset" or "Edm.TimeOfDay" or "Edm.Duration"
            || type?.EnumTypeFullName is not null;
        return new(FieldName, FilterOperator, quote ? JsonSerializer.Serialize(Value) : Value);
    }
    public void Restore(DataFilter filter)
    {
        FieldName = filter.Field;
        FilterOperator = filter.Operator;
        using var value = JsonDocument.Parse(filter.JsonValue);
        Value = value.RootElement.ValueKind == JsonValueKind.String ? value.RootElement.GetString()! : filter.JsonValue;
    }
}

public partial class AvailableFieldSelectionViewModel(FieldSchema field) : ObservableObject
{
    public FieldSchema Field { get; } = field;
    public string Name => Field.Name;
    public string TypeFullName => Field.TypeFullName;
    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private string required = "";
}

public partial class EntityDataPlanViewModel : ObservableObject
{
    private readonly SchemaSnapshot _source;
    private readonly SchemaSnapshot _target;
    private readonly EntityDataEligibilityService _eligibility;
    private bool _changingFields;
    private int _countQueryRevision;
    public string EntitySetName { get; }
    public int CountQueryRevision => _countQueryRevision;
    public IReadOnlyList<FieldSchema> SourceFields { get; }
    public ObservableCollection<AvailableFieldSelectionViewModel> AvailableFields { get; } = [];
    public string FieldSelectionSummary => AvailableFields.Any(item => item.IsSelected)
        ? $"{AvailableFields.Count(item => item.IsSelected)} selected / {AvailableFields.Count}; exclusions apply, key/company required"
        : $"All {AvailableFields.Count} fields downloaded; no $select. Exclusions apply to comparison only.";
    public IReadOnlyList<DataScopeKind> ScopeOptions { get; } = Enum.GetValues<DataScopeKind>();
    public IReadOnlyList<string> CompanyFields => SourceFields.Where(property => property.TypeFullName == "Edm.String" && !property.IsCollection)
        .Select(property => property.Name).ToArray();
    public string DefaultKey { get; }
    public string Warning => (string.IsNullOrWhiteSpace(KeyFieldsText) ? DefaultKey : KeyFieldsText).Split(',', StringSplitOptions.TrimEntries).Any(name => name.Contains("RecId", StringComparison.OrdinalIgnoreCase))
        ? "Technical identifiers can differ between environments. Review the matching key." : "";
    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private DataScopeKind scope;
    [ObservableProperty] private string companyField = "";
    [ObservableProperty] private string keyFieldsText = "";
    [ObservableProperty] private string excludedFieldsText = "";
    [ObservableProperty] private string sourceRows = "—";
    [ObservableProperty] private string sourceRowsDetails = "Not counted.";
    [ObservableProperty] private string targetRows = "—";
    [ObservableProperty] private string targetRowsDetails = "Not counted.";
    [ObservableProperty] private DataFilterViewModel? selectedFilter;
    public ObservableCollection<DataFilterViewModel> Filters { get; } = [];
    public PreparedEntityPlan Prepared => _eligibility.Prepare(_source, _target, Build());
    public string Eligibility => Prepared.IsReady ? "Ready" : string.Join(" ", Prepared.Problems);

    public EntityDataPlanViewModel(string name, SchemaSnapshot source, SchemaSnapshot target, EntityDataEligibilityService eligibility)
    {
        EntitySetName = name;
        _source = source;
        _target = target;
        _eligibility = eligibility;
        var entity = source.Entities.SingleOrDefault(entity => entity.EntitySetName == name);
        SourceFields = entity?.Fields ?? [];
        DefaultKey = string.Join(", ", entity?.Key.Select(key => key.Name) ?? []);
        foreach (var property in SourceFields)
        {
            var item = new AvailableFieldSelectionViewModel(property);
            item.PropertyChanged += (_, args) =>
            {
                if (!_changingFields && args.PropertyName == nameof(AvailableFieldSelectionViewModel.IsSelected)) NotifyEligibility();
            };
            AvailableFields.Add(item);
        }
        RefreshRequiredFields();
        Filters.CollectionChanged += (_, args) =>
        {
            if (args.NewItems is not null) foreach (DataFilterViewModel item in args.NewItems)
                item.PropertyChanged += (_, _) => { NotifyCountQueryChanged(); NotifyEligibility(); };
            NotifyCountQueryChanged();
            NotifyEligibility();
        };
    }
    public EntityDataPlan Build() => new(EntitySetName, Scope, Scope != DataScopeKind.Company || string.IsNullOrWhiteSpace(CompanyField) ? null : CompanyField,
        Split(KeyFieldsText), Split(ExcludedFieldsText), Filters.Select(filter => filter.Build()).ToArray(),
        AvailableFields.Select(item => new AvailableFieldSelection(item.Field, item.IsSelected)).ToArray());
    public bool TrySetCompanyScope(string field)
    {
        if (!CompanyFields.Contains(field, StringComparer.Ordinal)) return false;
        CompanyField = field;
        Scope = DataScopeKind.Company;
        return true;
    }
    public void SetRowsPending()
    {
        SourceRows = TargetRows = "Counting...";
        SourceRowsDetails = TargetRowsDetails = "Count request in progress.";
    }
    public void SetSourceRows(EntityCountReadResult result) => SetRows(result, true);
    public void SetTargetRows(EntityCountReadResult result) => SetRows(result, false);
    public void ResetRows()
    {
        SourceRows = TargetRows = "—";
        SourceRowsDetails = TargetRowsDetails = "Not counted.";
    }
    private void SetRows(EntityCountReadResult result, bool source)
    {
        var display = result.Count?.ToString("N0", CultureInfo.CurrentCulture) ?? (result.Diagnostic is null ? "N/A" : "Error");
        var details = result.Diagnostic?.ToString() ?? result.Message ?? "Count completed.";
        if (source) { SourceRows = display; SourceRowsDetails = details; }
        else { TargetRows = display; TargetRowsDetails = details; }
    }
    public void Restore(EntityDataPlan plan)
    {
        var saved = FieldSelectionPolicy.Resolve(plan, SourceFields);
        if (saved.Select(item => item.Field.Name).Distinct(StringComparer.Ordinal).Count() != saved.Count
            || saved.Any(item => !SourceFields.Any(property => property.Name == item.Field.Name && property.TypeFullName == item.Field.TypeFullName && property.IsCollection == item.Field.IsCollection)))
            throw new InvalidOperationException("The saved available-field catalog does not match the current schema.");
        Scope = plan.Scope;
        CompanyField = plan.CompanyField ?? "";
        KeyFieldsText = string.Join(", ", plan.KeyFields);
        ExcludedFieldsText = string.Join(", ", plan.ExcludedFields);
        Filters.Clear();
        foreach (var filter in plan.Filters) { var item = new DataFilterViewModel(SourceFields); item.Restore(filter); Filters.Add(item); }
        var checkedNames = saved.Where(item => item.IsSelected).Select(item => item.Field.Name).ToHashSet(StringComparer.Ordinal);
        SetFieldSelection(name => checkedNames.Contains(name));
    }
    [RelayCommand] private void AddFilter() => Filters.Add(new DataFilterViewModel(SourceFields));
    [RelayCommand] private void RemoveFilter() { if (SelectedFilter is not null) Filters.Remove(SelectedFilter); }
    [RelayCommand] private void SelectAllFields() => SetFieldSelection(_ => true);
    [RelayCommand] private void ClearSelectedFields() => SetFieldSelection(_ => false);
    private void SetFieldSelection(Func<string, bool> selected)
    {
        _changingFields = true;
        try { foreach (var item in AvailableFields) item.IsSelected = selected(item.Name); }
        finally { _changingFields = false; }
        NotifyEligibility();
    }
    public void NotifyEligibility()
    {
        RefreshRequiredFields();
        OnPropertyChanged(nameof(Eligibility)); OnPropertyChanged(nameof(Warning)); OnPropertyChanged(nameof(FieldSelectionSummary));
    }
    private void NotifyCountQueryChanged()
    {
        _countQueryRevision++;
        OnPropertyChanged(nameof(CountQueryRevision));
    }
    private void RefreshRequiredFields()
    {
        var keys = Split(string.IsNullOrWhiteSpace(KeyFieldsText) ? DefaultKey : KeyFieldsText).ToHashSet(StringComparer.Ordinal);
        foreach (var item in AvailableFields)
            item.Required = keys.Contains(item.Name) && Scope == DataScopeKind.Company && item.Name == CompanyField ? "Key / Company"
                : keys.Contains(item.Name) ? "Key" : Scope == DataScopeKind.Company && item.Name == CompanyField ? "Company" : "";
    }
    partial void OnScopeChanged(DataScopeKind value) { NotifyCountQueryChanged(); NotifyEligibility(); }
    partial void OnCompanyFieldChanged(string value) { NotifyCountQueryChanged(); NotifyEligibility(); }
    partial void OnKeyFieldsTextChanged(string value) => NotifyEligibility();
    partial void OnExcludedFieldsTextChanged(string value) => NotifyEligibility();
    private static string[] Split(string text) => text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}