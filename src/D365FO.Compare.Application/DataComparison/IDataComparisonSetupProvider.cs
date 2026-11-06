using D365FO.Compare.Domain.DataComparison;
using D365FO.Compare.Domain.Environments;
using D365FO.Compare.Domain.Metadata;

namespace D365FO.Compare.Application.DataComparison;

public interface ILegalEntityProvider
{
    Task<IReadOnlyList<LegalEntityOption>> LoadAsync(EnvironmentConnection environment, SchemaSnapshot schema,
        IProgress<DataReadProgress>? progress, string side, CancellationToken cancellationToken);
}
public interface IDataComparisonSetupProvider
{
    Task<DataComparisonSetupSnapshot> LoadAsync(ComparisonContextState context, IProgress<DataReadProgress>? progress,
        CancellationToken cancellationToken);
}