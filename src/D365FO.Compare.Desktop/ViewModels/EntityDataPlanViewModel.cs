using System.Collections.ObjectModel;
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

public partial class EntityDataPlanViewModel : ObservableObject
{
    private readonly SchemaSnapshot _source;
    private readonly SchemaSnapshot _target;
    private readonly EntityDataEligibilityService _eligibility;
    public string EntitySetName { get; }
    public IReadOnlyList<FieldSchema> SourceFields { get; }
    public IReadOnlyList<DataScopeKind> ScopeOptions { get; } = Enum.GetValues<DataScopeKind>();
    public IReadOnlyList<string> CompanyFields => SourceFields.Where(property => property.TypeFullName == "Edm.String").Select(property => property.Name).ToArray();
    public string DefaultKey { get; }
    public string Warning => (string.IsNullOrWhiteSpace(KeyFieldsText) ? DefaultKey : KeyFieldsText).Split(',', StringSplitOptions.TrimEntries).Any(name => name.Contains("RecId", StringComparison.OrdinalIgnoreCase))
        ? "Technical identifiers can differ between environments. Review the matching key." : "";
    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private DataScopeKind scope;
    [ObservableProperty] private string companyField = "";
    [ObservableProperty] private string keyFieldsText = "";
    [ObservableProperty] private string excludedFieldsText = "";
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
        Filters.CollectionChanged += (_, args) =>
        {
            if (args.NewItems is not null) foreach (DataFilterViewModel item in args.NewItems) item.PropertyChanged += (_, _) => NotifyEligibility();
            NotifyEligibility();
        };
    }
    public EntityDataPlan Build() => new(EntitySetName, Scope, Scope != DataScopeKind.Company || string.IsNullOrWhiteSpace(CompanyField) ? null : CompanyField,
        Split(KeyFieldsText), Split(ExcludedFieldsText), Filters.Select(filter => filter.Build()).ToArray());
    public void Restore(EntityDataPlan plan)
    {
        Scope = plan.Scope;
        CompanyField = plan.CompanyField ?? "";
        KeyFieldsText = string.Join(", ", plan.KeyFields);
        ExcludedFieldsText = string.Join(", ", plan.ExcludedFields);
        Filters.Clear();
        foreach (var filter in plan.Filters) { var item = new DataFilterViewModel(SourceFields); item.Restore(filter); Filters.Add(item); }
    }
    [RelayCommand] private void AddFilter() => Filters.Add(new DataFilterViewModel(SourceFields));
    [RelayCommand] private void RemoveFilter() { if (SelectedFilter is not null) Filters.Remove(SelectedFilter); }
    public void NotifyEligibility() { OnPropertyChanged(nameof(Eligibility)); OnPropertyChanged(nameof(Warning)); }
    partial void OnScopeChanged(DataScopeKind value) => NotifyEligibility();
    partial void OnCompanyFieldChanged(string value) => NotifyEligibility();
    partial void OnKeyFieldsTextChanged(string value) => NotifyEligibility();
    partial void OnExcludedFieldsTextChanged(string value) => NotifyEligibility();
    private static string[] Split(string text) => text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}