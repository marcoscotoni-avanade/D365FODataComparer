using D365FO.Compare.Domain.Configuration;

namespace D365FO.Compare.Application.Configuration;

public interface IConfigurationRepository
{
    Task<AppConfiguration?> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(AppConfiguration configuration, CancellationToken cancellationToken);
    Task DeleteAsync(CancellationToken cancellationToken);
}