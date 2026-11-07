using D365FO.Compare.Domain.Configuration;

namespace D365FO.Compare.Application.Configuration;

public interface IGeneralPreferencesRepository
{
    Task<GeneralPreferences> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(GeneralPreferences preferences, CancellationToken cancellationToken);
}