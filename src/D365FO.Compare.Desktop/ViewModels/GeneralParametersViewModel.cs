using System.IO;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using D365FO.Compare.Application.Configuration;
using D365FO.Compare.Domain.Configuration;

namespace D365FO.Compare.Desktop.ViewModels;

public partial class GeneralParametersViewModel(IGeneralPreferencesRepository repository) : ObservableObject
{
    [ObservableProperty] private string maxConcurrentCountRequestsText = GeneralPreferences.DefaultMaxConcurrentCountRequests.ToString(CultureInfo.InvariantCulture);
    [ObservableProperty] private int savedMaxConcurrentCountRequests = GeneralPreferences.DefaultMaxConcurrentCountRequests;
    [ObservableProperty] private string status = "Defaults loaded.";
    [ObservableProperty] private bool isBusy;

    public bool CanSave => !IsBusy && TryGetValidCount(out var value) && value != SavedMaxConcurrentCountRequests;
    public bool CanRestoreDefaults => !IsBusy && (SavedMaxConcurrentCountRequests != GeneralPreferences.DefaultMaxConcurrentCountRequests
        || MaxConcurrentCountRequestsText != GeneralPreferences.DefaultMaxConcurrentCountRequests.ToString(CultureInfo.InvariantCulture));

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var preferences = await repository.LoadAsync(cancellationToken);
            Apply(preferences);
            Status = "General parameters loaded.";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Apply(GeneralPreferences.Default);
            Status = "Saved general parameters are invalid or unavailable. Defaults are in use; save to replace them.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!TryGetValidCount(out var value)) return;
        await PersistAsync(new GeneralPreferences(value), "General parameters saved.");
    }

    [RelayCommand(CanExecute = nameof(CanRestoreDefaults))]
    private async Task RestoreDefaultsAsync() => await PersistAsync(GeneralPreferences.Default, "Default general parameters restored.");

    private async Task PersistAsync(GeneralPreferences preferences, string successMessage)
    {
        IsBusy = true;
        NotifyCommands();
        try
        {
            await repository.SaveAsync(preferences, CancellationToken.None);
            Apply(preferences);
            Status = successMessage;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Status = "General parameters could not be saved. Check local file access.";
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    private void Apply(GeneralPreferences preferences)
    {
        SavedMaxConcurrentCountRequests = preferences.MaxConcurrentCountRequests;
        MaxConcurrentCountRequestsText = preferences.MaxConcurrentCountRequests.ToString(CultureInfo.InvariantCulture);
        NotifyCommands();
    }

    private bool TryGetValidCount(out int value) => int.TryParse(MaxConcurrentCountRequestsText, NumberStyles.None,
        CultureInfo.InvariantCulture, out value) && value is >= GeneralPreferences.MinConcurrentCountRequests and <= GeneralPreferences.MaxConcurrentCountRequestsLimit;

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanRestoreDefaults));
        SaveCommand.NotifyCanExecuteChanged();
        RestoreDefaultsCommand.NotifyCanExecuteChanged();
    }

    partial void OnMaxConcurrentCountRequestsTextChanged(string value) => NotifyCommands();
    partial void OnIsBusyChanged(bool value) => NotifyCommands();
}