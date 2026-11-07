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
    [ObservableProperty] private string excludedEntitySetsText = "";
    [ObservableProperty] private int savedMaxConcurrentCountRequests = GeneralPreferences.DefaultMaxConcurrentCountRequests;
    [ObservableProperty] private IReadOnlyList<string> savedExcludedEntitySets = [];
    [ObservableProperty] private string status = "Defaults loaded.";
    [ObservableProperty] private bool isBusy;

    public bool CanSave => !IsBusy && TryGetPreferences(out var preferences) && !EqualsPreferences(preferences, SavedMaxConcurrentCountRequests, SavedExcludedEntitySets);
    public bool CanRestoreDefaults => !IsBusy && (SavedMaxConcurrentCountRequests != GeneralPreferences.DefaultMaxConcurrentCountRequests
        || MaxConcurrentCountRequestsText != GeneralPreferences.DefaultMaxConcurrentCountRequests.ToString(CultureInfo.InvariantCulture)
        || SavedExcludedEntitySets.Count != 0 || !string.IsNullOrWhiteSpace(ExcludedEntitySetsText));

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
        if (!TryGetPreferences(out var preferences)) return;
        await PersistAsync(preferences, "General parameters saved.");
    }

    [RelayCommand(CanExecute = nameof(CanRestoreDefaults))]
    private async Task RestoreDefaultsAsync() => await PersistAsync(GeneralPreferences.Default, "Default general parameters restored.");

    public async Task<bool> AddExcludedEntitySetsAsync(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (IsBusy) return false;
        var pendingExclusions = ExcludedEntitySetsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var merged = SavedExcludedEntitySets.Concat(pendingExclusions).Concat(names.Select(name => name.Trim()))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (merged.Length == SavedExcludedEntitySets.Count) return true;
        var preferences = new GeneralPreferences(SavedMaxConcurrentCountRequests) { ExcludedEntitySets = merged };
        IsBusy = true;
        NotifyCommands();
        try
        {
            await repository.SaveAsync(preferences, CancellationToken.None);
            SavedExcludedEntitySets = merged;
            ExcludedEntitySetsText = string.Join(Environment.NewLine, merged);
            Status = "Entity exclusions added and saved.";
            NotifyCommands();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Status = "Entity exclusions could not be saved. Check local file access.";
            return false;
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

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
        SavedExcludedEntitySets = preferences.ExcludedEntitySets;
        ExcludedEntitySetsText = string.Join(Environment.NewLine, preferences.ExcludedEntitySets);
        NotifyCommands();
    }

    private bool TryGetPreferences(out GeneralPreferences preferences)
    {
        preferences = GeneralPreferences.Default;
        if (!TryGetValidCount(out var count)) return false;
        var exclusions = ExcludedEntitySetsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        preferences = new GeneralPreferences(count) { ExcludedEntitySets = exclusions };
        return true;
    }

    private bool TryGetValidCount(out int value) => int.TryParse(MaxConcurrentCountRequestsText, NumberStyles.None,
        CultureInfo.InvariantCulture, out value) && value is >= GeneralPreferences.MinConcurrentCountRequests and <= GeneralPreferences.MaxConcurrentCountRequestsLimit;

    private static bool EqualsPreferences(GeneralPreferences preferences, int count, IReadOnlyList<string> exclusions) =>
        preferences.MaxConcurrentCountRequests == count && preferences.ExcludedEntitySets.SequenceEqual(exclusions, StringComparer.OrdinalIgnoreCase);

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanRestoreDefaults));
        SaveCommand.NotifyCanExecuteChanged();
        RestoreDefaultsCommand.NotifyCanExecuteChanged();
    }

    partial void OnMaxConcurrentCountRequestsTextChanged(string value) => NotifyCommands();
    partial void OnExcludedEntitySetsTextChanged(string value) => NotifyCommands();
    partial void OnIsBusyChanged(bool value) => NotifyCommands();
}