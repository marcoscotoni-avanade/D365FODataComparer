using D365FO.Compare.Domain.Environments;
using D365FO.Compare.Domain.Metadata;

namespace D365FO.Compare.Application.DataComparison;

public sealed record ComparisonContextState(EnvironmentConnection? Source, EnvironmentConnection? Target,
    SchemaSnapshot? SourceSchema, SchemaSnapshot? TargetSchema, bool OnlineReady);

public sealed class ActiveComparisonContext
{
    public ComparisonContextState State { get; private set; } = new(null, null, null, null, false);
    public event EventHandler? Changed;
    public void Update(ComparisonContextState state)
    {
        if (State == state) return;
        State = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}