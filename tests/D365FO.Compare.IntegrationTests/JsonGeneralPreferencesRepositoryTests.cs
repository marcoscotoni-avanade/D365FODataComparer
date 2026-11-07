using D365FO.Compare.Domain.Configuration;
using D365FO.Compare.Infrastructure.Persistence;

namespace D365FO.Compare.IntegrationTests;

public sealed class JsonGeneralPreferencesRepositoryTests
{
    [Fact]
    public async Task MissingFileUsesDefaultsAndSavedPreferenceRoundTrips()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"d365fo-preferences-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "general-preferences.json");
        var repository = new JsonGeneralPreferencesRepository(path);
        try
        {
            Assert.Equal(GeneralPreferences.Default, await repository.LoadAsync(CancellationToken.None));
            var saved = new GeneralPreferences(GeneralPreferences.MaxConcurrentCountRequestsLimit);
            await repository.SaveAsync(saved, CancellationToken.None);
            Assert.Equal(saved, await repository.LoadAsync(CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(
                new GeneralPreferences(GeneralPreferences.MaxConcurrentCountRequestsLimit + 1), CancellationToken.None));
            Assert.Equal(saved, await repository.LoadAsync(CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OutOfRangePersistedValueIsRejected()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"d365fo-preferences-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "general-preferences.json");
        try
        {
            var invalidValue = GeneralPreferences.MaxConcurrentCountRequestsLimit + 1;
            await File.WriteAllTextAsync(path, $"{{\"maxConcurrentCountRequests\":{invalidValue}}}");
            await Assert.ThrowsAsync<InvalidDataException>(() => new JsonGeneralPreferencesRepository(path).LoadAsync(CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}