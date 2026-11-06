namespace D365FO.Compare.Domain.DataComparison;

public enum DataSetupStatus { NotLoaded, Loading, Ready, ReadyWithWarnings, Failed, Canceled }
public sealed record LegalEntityOption(string Code, string? Name = null)
{
    public string DisplayText => string.IsNullOrWhiteSpace(Name) ? Code : $"{Code} - {Name}";
}
public sealed record CompanySetupResult(IReadOnlyList<LegalEntityOption> Companies, bool IsComplete,
    DateTimeOffset CapturedAtUtc, DataFailureDiagnostic? Diagnostic = null, bool RequiresAuthentication = false);
public sealed record SetupRequirementResult(string Name, bool Required, bool Succeeded);
public sealed record DataComparisonSetupSnapshot(CompanySetupResult Source, CompanySetupResult Target,
    IReadOnlyList<SetupRequirementResult>? AdditionalSteps = null)
{
    public bool OtherRequirementsComplete => AdditionalSteps?.All(step => !step.Required || step.Succeeded) != false;
    public bool IsComplete => Source.IsComplete && Target.IsComplete && OtherRequirementsComplete;
}