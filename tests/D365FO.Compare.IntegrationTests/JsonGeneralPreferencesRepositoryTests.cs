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
            var saved = new GeneralPreferences(GeneralPreferences.MaxConcurrentCountRequestsLimit)
                { ExcludedEntitySets = ["Customers", "SalesOrders"] };
            await repository.SaveAsync(saved, CancellationToken.None);
            var loaded = await repository.LoadAsync(CancellationToken.None);
            Assert.Equal(saved.MaxConcurrentCountRequests, loaded.MaxConcurrentCountRequests);
            Assert.Equal(saved.ExcludedEntitySets, loaded.ExcludedEntitySets);
            await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(
                new GeneralPreferences(GeneralPreferences.MaxConcurrentCountRequestsLimit + 1), CancellationToken.None));
            var unchanged = await repository.LoadAsync(CancellationToken.None);
            Assert.Equal(saved.MaxConcurrentCountRequests, unchanged.MaxConcurrentCountRequests);
            Assert.Equal(saved.ExcludedEntitySets, unchanged.ExcludedEntitySets);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OldPreferenceFilesWithoutEntityExclusionsUseEmptyList()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"d365fo-preferences-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "general-preferences.json");
        try
        {
            await File.WriteAllTextAsync(path, """{"maxConcurrentCountRequests":4}""");
            var loaded = await new JsonGeneralPreferencesRepository(path).LoadAsync(CancellationToken.None);
            Assert.Equal(4, loaded.MaxConcurrentCountRequests);
            Assert.Empty(loaded.ExcludedEntitySets);
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