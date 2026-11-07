using D365FO.Compare.Application.Configuration;
using D365FO.Compare.Desktop.ViewModels;
using D365FO.Compare.Domain.Configuration;

namespace D365FO.Compare.UnitTests;

public sealed class GeneralParametersViewModelTests
{
    [Fact]
    public async Task SaveValidatesAndRestoresDefault()
    {
        var repository = new PreferencesRepository();
        var viewModel = new GeneralParametersViewModel(repository);
        await viewModel.InitializeAsync();

        Assert.Equal("2", viewModel.MaxConcurrentCountRequestsText);
        Assert.False(viewModel.CanSave);
        viewModel.MaxConcurrentCountRequestsText = "0";
        Assert.False(viewModel.CanSave);
        viewModel.MaxConcurrentCountRequestsText = (GeneralPreferences.MaxConcurrentCountRequestsLimit + 1).ToString();
        Assert.False(viewModel.CanSave);
        viewModel.MaxConcurrentCountRequestsText = "4";
        Assert.True(viewModel.CanSave);

        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Equal(4, viewModel.SavedMaxConcurrentCountRequests);
        Assert.Equal(new GeneralPreferences(4), repository.Value);
        Assert.True(viewModel.CanRestoreDefaults);

        await viewModel.RestoreDefaultsCommand.ExecuteAsync(null);
        Assert.Equal(GeneralPreferences.Default, repository.Value);
        Assert.Equal("2", viewModel.MaxConcurrentCountRequestsText);
        Assert.False(viewModel.CanRestoreDefaults);
    }

    [Fact]
    public async Task InvalidPersistedPreferenceFallsBackToDefault()
    {
        var viewModel = new GeneralParametersViewModel(new PreferencesRepository(new GeneralPreferences(GeneralPreferences.MaxConcurrentCountRequestsLimit + 1)));
        await viewModel.InitializeAsync();
        Assert.Equal(2, viewModel.SavedMaxConcurrentCountRequests);
        Assert.Contains("Defaults are in use", viewModel.Status);
    }

    private sealed class PreferencesRepository(GeneralPreferences? initial = null) : IGeneralPreferencesRepository
    {
        public GeneralPreferences Value { get; private set; } = initial ?? GeneralPreferences.Default;
        public Task<GeneralPreferences> LoadAsync(CancellationToken cancellationToken)
        {
            if (!Value.IsValid) throw new InvalidDataException("Invalid test preference.");
            return Task.FromResult(Value);
        }
        public Task SaveAsync(GeneralPreferences preferences, CancellationToken cancellationToken)
        {
            if (!preferences.IsValid) throw new InvalidDataException("Invalid test preference.");
            Value = preferences;
            return Task.CompletedTask;
        }
    }
}