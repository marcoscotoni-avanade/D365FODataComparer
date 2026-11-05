using D365FO.Compare.Domain.Authentication;
using D365FO.Compare.Domain.Environments;

namespace D365FO.Compare.Application.EnvironmentDiscovery;

public interface IEnvironmentDiscoveryProvider
{
    Task<IReadOnlyList<DiscoveredEnvironment>> DiscoverAsync(
        UserSession session,
        IntPtr parentWindow,
        CancellationToken cancellationToken);
}