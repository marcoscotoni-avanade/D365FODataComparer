using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using D365FO.Compare.Application.Authentication;
using D365FO.Compare.Application.Configuration;
using D365FO.Compare.Application.EnvironmentDiscovery;
using D365FO.Compare.Application.Validation;
using D365FO.Compare.Domain.Authentication;
using D365FO.Compare.Domain.Configuration;
using D365FO.Compare.Domain.Environments;
using Microsoft.Extensions.Logging;

namespace D365FO.Compare.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IAuthenticationService _authenticationService;
    private readonly IEnvironmentDiscoveryProvider _discoveryProvider;
    private readonly IConfigurationRepository _configurationRepository;
    private readonly IConnectionValidationService _connectionValidationService;
    private readonly IFinOpsHostPolicy _finOpsHostPolicy;
    private readonly ILogger<MainViewModel> _logger;
    private Func<IntPtr> _parentWindowProvider = static () => IntPtr.Zero;
    private UserSession? _session;
    private AppConfiguration? _loadedConfiguration;
    private CancellationTokenSource? _activeOperationCancellation;
    private bool _confirmDuplicateUrlOnNextSave;
    private ConnectionValidationStatus _sourceValidationStatus = ConnectionValidationStatus.NotTested;
    private ConnectionValidationStatus _targetValidationStatus = ConnectionValidationStatus.NotTested;

    public MainViewModel(
        IAuthenticationService authenticationService,
        IEnvironmentDiscoveryProvider discoveryProvider,
        IConfigurationRepository configurationRepository,
        IConnectionValidationService connectionValidationService,
        IFinOpsHostPolicy finOpsHostPolicy,
        ILogger<MainViewModel> logger)
    {
        _authenticationService = authenticationService;
        _discoveryProvider = discoveryProvider;
        _configurationRepository = configurationRepository;
        _connectionValidationService = connectionValidationService;
        _finOpsHostPolicy = finOpsHostPolicy;
        _logger = logger;
        EnvironmentsView = CollectionViewSource.GetDefaultView(DiscoveredEnvironments);
        EnvironmentsView.Filter = FilterEnvironment;
    }

    public ObservableCollection<DiscoveredEnvironment> DiscoveredEnvironments { get; } = [];
    public ICollectionView EnvironmentsView { get; }
    public IReadOnlyList<string> AvailableTypes => ["All types", .. DiscoveredEnvironments.Select(item => item.PowerPlatformType).OfType<string>().Where(NotBlank).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
    public IReadOnlyList<string> AvailableStates => ["All states", .. DiscoveredEnvironments.Select(item => item.State).OfType<string>().Where(NotBlank).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
    public IReadOnlyList<string> AvailableRegions => ["All regions", .. DiscoveredEnvironments.Select(item => item.Region).OfType<string>().Where(NotBlank).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyList<DiscoveredEnvironment> FinOpsEnvironments
    {
        get
        {
            var results = DiscoveredEnvironments
                .Where(item => item.IsSelectableFinOps && _finOpsHostPolicy.IsAllowed(item.FinOpsUrl) && _session is not null
                    && string.Equals(item.TenantId, _session.TenantId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            AddSelectedIfMissing(results, SelectedSource, _finOpsHostPolicy);
            AddSelectedIfMissing(results, SelectedTarget, _finOpsHostPolicy);
            return results;
        }
    }

    public bool IsNotBusy => !IsBusy;

    public string PrimaryActionLabel => CurrentStepIndex == 3 ? "Save configuration" : "Continue";

    [ObservableProperty]
    private int currentStepIndex;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private bool showNonFinOpsEnvironments;

    [ObservableProperty]
    private string selectedTypeFilter = "All types";

    [ObservableProperty]
    private string selectedStateFilter = "All states";

    [ObservableProperty]
    private string selectedRegionFilter = "All regions";

    [ObservableProperty]
    private DiscoveredEnvironment? selectedEnvironment;

    [ObservableProperty]
    private DiscoveredEnvironment? selectedSource;

    [ObservableProperty]
    private DiscoveredEnvironment? selectedTarget;

    [ObservableProperty]
    private string sessionDisplay = "Not signed in";

    [ObservableProperty]
    private string statusMessage = "Sign in to discover environments.";

    [ObservableProperty]
    private string footerStatus = "Ready";

    [ObservableProperty]
    private string sourceValidationMessage = "Not tested";

    [ObservableProperty]
    private string targetValidationMessage = "Not tested";

    [ObservableProperty]
    private string selectionWarning = string.Empty;

    [ObservableProperty]
    private string manualDisplayName = string.Empty;

    [ObservableProperty]
    private string manualFinOpsUrl = string.Empty;

    [ObservableProperty]
    private bool isManualEntryVisible;

    [ObservableProperty]
    private bool isBusy;

    public string SourceUrlDisplay => SelectedSource?.FinOpsUrl?.ToString() ?? "Select a Source Environment";
    public string TargetUrlDisplay => SelectedTarget?.FinOpsUrl?.ToString() ?? "Select a Target Environment";
    public string SourceSummary => FormatSummary(SelectedSource, SourceValidationMessage);
    public string TargetSummary => FormatSummary(SelectedTarget, TargetValidationMessage);

    public void SetParentWindowProvider(Func<IntPtr> parentWindowProvider) =>
        _parentWindowProvider = parentWindowProvider;

    public async Task InitializeAsync()
    {
        try
        {
            _loadedConfiguration = await _configurationRepository.LoadAsync(CancellationToken.None);
            _session = await _authenticationService.GetCurrentSessionAsync(CancellationToken.None);
            UpdateSessionDisplay();

            if (_loadedConfiguration is not null)
            {
                RestoreSelection(_loadedConfiguration);
            }

            if (_session is null && _loadedConfiguration?.AuthenticationContext is not null)
            {
                StatusMessage = "A saved configuration is available. Sign in to revalidate its environments.";
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            StatusMessage = "The saved configuration could not be loaded. Clear it or check the local configuration file.";
            FooterStatus = "Configuration load failed";
        }
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        await RunBusyAsync("Signing in...", async cancellationToken =>
        {
            var previousTenantId = _session?.TenantId;
            _session = await _authenticationService.SignInAsync(_parentWindowProvider(), cancellationToken);
            UpdateSessionDisplay();

            if (!string.IsNullOrWhiteSpace(previousTenantId)
                && !string.Equals(previousTenantId, _session.TenantId, StringComparison.OrdinalIgnoreCase))
            {
                InvalidateSelections();
            }
            else if (_loadedConfiguration?.AuthenticationContext is { } savedAuthentication
                && !string.Equals(savedAuthentication.TenantId, _session.TenantId, StringComparison.OrdinalIgnoreCase))
            {
                InvalidateSelections();
            }

            StatusMessage = "Signed in. Discover environments to continue.";
        });
    }

    [RelayCommand]
    private async Task ChangeAccountAsync()
    {
        await RunBusyAsync("Changing account...", async cancellationToken =>
        {
            var previousHomeAccountId = _session?.HomeAccountId ?? _loadedConfiguration?.AuthenticationContext?.HomeAccountId;
            _session = await _authenticationService.ChangeAccountAsync(_parentWindowProvider(), cancellationToken);
            UpdateSessionDisplay();

            if (!string.Equals(previousHomeAccountId, _session.HomeAccountId, StringComparison.OrdinalIgnoreCase))
            {
                InvalidateSelections();
            }

            StatusMessage = "Account changed. Discover environments to continue.";
        });
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        await RunBusyAsync("Signing out...", async cancellationToken =>
        {
            await _authenticationService.SignOutAsync(cancellationToken);
            _session = null;
            SelectedSource = null;
            SelectedTarget = null;
            SourceValidationMessage = "Not tested";
            TargetValidationMessage = "Not tested";
            _sourceValidationStatus = ConnectionValidationStatus.RequiresRevalidation;
            _targetValidationStatus = ConnectionValidationStatus.RequiresRevalidation;
            UpdateSessionDisplay();

            if (_loadedConfiguration is not null)
            {
                _loadedConfiguration = _loadedConfiguration with
                {
                    SourceEnvironment = MarkForRevalidation(_loadedConfiguration.SourceEnvironment),
                    TargetEnvironment = MarkForRevalidation(_loadedConfiguration.TargetEnvironment)
                };
                await _configurationRepository.SaveAsync(_loadedConfiguration, cancellationToken);
            }

            StatusMessage = "Signed out. Saved environments require revalidation.";
        });
    }

    [RelayCommand]
    private async Task DiscoverAsync()
    {
        if (_session is null)
        {
            StatusMessage = "Sign in before discovering environments.";
            CurrentStepIndex = 0;
            return;
        }

        await RunBusyAsync("Discovering environments...", async cancellationToken =>
        {
            var discovered = await _discoveryProvider.DiscoverAsync(
                _session,
                _parentWindowProvider(),
            cancellationToken);

            DiscoveredEnvironments.Clear();
            foreach (var environment in discovered)
            {
                if (string.Equals(environment.TenantId, _session.TenantId, StringComparison.OrdinalIgnoreCase))
                {
                    if (environment.IsSelectableFinOps)
                    {
                        _finOpsHostPolicy.Approve(environment.FinOpsUrl);
                    }

                    DiscoveredEnvironments.Add(environment);
                }
            }

            OnPropertyChanged(nameof(AvailableTypes));
            OnPropertyChanged(nameof(AvailableStates));
            OnPropertyChanged(nameof(AvailableRegions));
            OnPropertyChanged(nameof(FinOpsEnvironments));
            EnvironmentsView.Refresh();
            StatusMessage = $"Discovered {DiscoveredEnvironments.Count} environment(s); {FinOpsEnvironments.Count} have a usable FinOps URL.";
            CurrentStepIndex = 1;
        });
    }

    [RelayCommand]
    private void ToggleManualEntry() => IsManualEntryVisible = !IsManualEntryVisible;

    [RelayCommand]
    private void AddManualEnvironment()
    {
        if (_session is null)
        {
            StatusMessage = "Sign in before adding a manual environment.";
            CurrentStepIndex = 0;
            return;
        }

        if (string.IsNullOrWhiteSpace(ManualDisplayName)
            || !Uri.TryCreate(ManualFinOpsUrl, UriKind.Absolute, out var finOpsUrl)
            || !_finOpsHostPolicy.Approve(finOpsUrl))
        {
            StatusMessage = "Enter a display name and a valid HTTPS base URL for the environment.";
            return;
        }

        var environment = new DiscoveredEnvironment
        {
            ProviderEnvironmentId = $"manual:{finOpsUrl.GetLeftPart(UriPartial.Authority).ToLowerInvariant()}",
            TenantId = _session.TenantId,
            DisplayName = ManualDisplayName.Trim(),
            FinOpsUrl = finOpsUrl
        };

        if (DiscoveredEnvironments.Any(item => string.Equals(item.ProviderEnvironmentId, environment.ProviderEnvironmentId, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = "An environment with this URL is already listed.";
            return;
        }

        DiscoveredEnvironments.Add(environment);
        OnPropertyChanged(nameof(FinOpsEnvironments));
        EnvironmentsView.Refresh();
        SelectedEnvironment = environment;
        ManualDisplayName = string.Empty;
        ManualFinOpsUrl = string.Empty;
        StatusMessage = "Manual environment added. Select it as Source or Target and test the connection.";
    }

    [RelayCommand]
    private async Task TestSourceAsync() => await TestConnectionAsync(SelectedSource, isSource: true);

    [RelayCommand]
    private async Task TestTargetAsync() => await TestConnectionAsync(SelectedTarget, isSource: false);

    [RelayCommand]
    private async Task SaveConfigurationAsync()
    {
        if (!TryValidateSelection(out var warning))
        {
            StatusMessage = warning;
            SelectionWarning = warning;
            return;
        }

        if (!_confirmDuplicateUrlOnNextSave && HasDuplicateUrlWithDifferentId())
        {
            _confirmDuplicateUrlOnNextSave = true;
            StatusMessage = "Source and Target have the same URL but different IDs. Review the selection and select Save again to confirm.";
            return;
        }

        _confirmDuplicateUrlOnNextSave = false;
        var configuration = new AppConfiguration
        {
            AuthenticationContext = new AuthenticationContext(
                _session!.TenantId,
                _session.Username,
                _session.HomeAccountId),
            SourceEnvironment = EnvironmentConnection.FromDiscovered(SelectedSource!) with
            {
                ValidationStatus = ConnectionValidationStatus.Succeeded
            },
            TargetEnvironment = EnvironmentConnection.FromDiscovered(SelectedTarget!) with
            {
                ValidationStatus = ConnectionValidationStatus.Succeeded
            }
        };

        await RunBusyAsync("Saving configuration...", async cancellationToken =>
        {
            await _configurationRepository.SaveAsync(configuration, cancellationToken);
            _loadedConfiguration = configuration;
            StatusMessage = "Configuration saved locally.";
        });
    }

    [RelayCommand]
    private async Task ClearConfigurationAsync()
    {
        await _configurationRepository.DeleteAsync(CancellationToken.None);
        _loadedConfiguration = null;
        SelectedSource = null;
        SelectedTarget = null;
        SourceValidationMessage = "Not tested";
        TargetValidationMessage = "Not tested";
        StatusMessage = "Saved configuration cleared.";
    }

    [RelayCommand]
    private async Task NextStepAsync()
    {
        if (CurrentStepIndex == 0 && _session is null)
        {
            StatusMessage = "Sign in before continuing.";
            return;
        }

        if (CurrentStepIndex == 3)
        {
            await SaveConfigurationAsync();
            return;
        }

        if (CurrentStepIndex == 2 && !TryValidateSelection(out var warning))
        {
            SelectionWarning = warning;
            StatusMessage = warning;
            return;
        }

        CurrentStepIndex = Math.Min(3, CurrentStepIndex + 1);
    }

    partial void OnCurrentStepIndexChanged(int value) => OnPropertyChanged(nameof(PrimaryActionLabel));

    [RelayCommand]
    private void PreviousStep() => CurrentStepIndex = Math.Max(0, CurrentStepIndex - 1);

    [RelayCommand]
    private void CancelOperation() => _activeOperationCancellation?.Cancel();

    partial void OnSearchTextChanged(string value) => EnvironmentsView.Refresh();
    partial void OnShowNonFinOpsEnvironmentsChanged(bool value) => EnvironmentsView.Refresh();
    partial void OnSelectedTypeFilterChanged(string value) => EnvironmentsView.Refresh();
    partial void OnSelectedStateFilterChanged(string value) => EnvironmentsView.Refresh();
    partial void OnSelectedRegionFilterChanged(string value) => EnvironmentsView.Refresh();

    partial void OnSelectedSourceChanged(DiscoveredEnvironment? value)
    {
        if (value is not null)
        {
            _finOpsHostPolicy.Approve(value.FinOpsUrl);
        }

        SourceValidationMessage = "Not tested";
        _sourceValidationStatus = ConnectionValidationStatus.NotTested;
        SelectionWarning = string.Empty;
        OnPropertyChanged(nameof(SourceUrlDisplay));
        OnPropertyChanged(nameof(SourceSummary));
        OnPropertyChanged(nameof(FinOpsEnvironments));
        _confirmDuplicateUrlOnNextSave = false;
    }

    partial void OnSelectedTargetChanged(DiscoveredEnvironment? value)
    {
        if (value is not null)
        {
            _finOpsHostPolicy.Approve(value.FinOpsUrl);
        }

        TargetValidationMessage = "Not tested";
        _targetValidationStatus = ConnectionValidationStatus.NotTested;
        SelectionWarning = string.Empty;
        OnPropertyChanged(nameof(TargetUrlDisplay));
        OnPropertyChanged(nameof(TargetSummary));
        OnPropertyChanged(nameof(FinOpsEnvironments));
        _confirmDuplicateUrlOnNextSave = false;
    }

    partial void OnSourceValidationMessageChanged(string value) => OnPropertyChanged(nameof(SourceSummary));
    partial void OnTargetValidationMessageChanged(string value) => OnPropertyChanged(nameof(TargetSummary));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsNotBusy));

    private async Task TestConnectionAsync(DiscoveredEnvironment? environment, bool isSource)
    {
        if (environment is null)
        {
            StatusMessage = $"Select a {(isSource ? "Source" : "Target")} Environment first.";
            return;
        }

        await RunBusyAsync($"Testing {(isSource ? "Source" : "Target")} connection...", async cancellationToken =>
        {
            ConnectionValidationResult result;
            try
            {
                result = await _connectionValidationService.ValidateAsync(
                    environment,
                    _parentWindowProvider(),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var failureMessage = ToSafeMessage(exception);
                if (isSource)
                {
                    SourceValidationMessage = failureMessage;
                    _sourceValidationStatus = ConnectionValidationStatus.Failed;
                }
                else
                {
                    TargetValidationMessage = failureMessage;
                    _targetValidationStatus = ConnectionValidationStatus.Failed;
                }

                throw;
            }

            if (isSource)
            {
                SourceValidationMessage = result.Message;
                _sourceValidationStatus = result.Status;
            }
            else
            {
                TargetValidationMessage = result.Message;
                _targetValidationStatus = result.Status;
            }

            StatusMessage = result.Message;
            if (result.Status == ConnectionValidationStatus.RequiresRevalidation)
            {
                _session = null;
                UpdateSessionDisplay();
            }
        });
    }

    private async Task RunBusyAsync(string busyMessage, Func<CancellationToken, Task> operation)
    {
        if (IsBusy)
        {
            return;
        }

        using var operationCancellation = new CancellationTokenSource();
        _activeOperationCancellation = operationCancellation;
        IsBusy = true;
        FooterStatus = busyMessage;
        try
        {
            await operation(operationCancellation.Token);
            FooterStatus = "Ready";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Operation canceled.";
            FooterStatus = "Canceled";
        }
        catch (TimeoutException exception)
        {
            _logger.LogWarning(exception, "Application operation timed out during {Operation}.", busyMessage);
            StatusMessage = "The operation timed out. Check connectivity and try again.";
            FooterStatus = "Timed out";
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "Application operation failed during {Operation}. ExceptionType={ExceptionType}.",
                busyMessage,
                exception.GetType().Name);
            StatusMessage = ToSafeMessage(exception);
            FooterStatus = "Operation failed";
        }
        finally
        {
            _activeOperationCancellation = null;
            IsBusy = false;
        }
    }

    private bool FilterEnvironment(object item)
    {
        if (item is not DiscoveredEnvironment environment)
        {
            return false;
        }

        if (!ShowNonFinOpsEnvironments && !environment.IsSelectableFinOps)
        {
            return false;
        }

        var matchesType = SelectedTypeFilter == "All types"
            || string.Equals(environment.PowerPlatformType, SelectedTypeFilter, StringComparison.OrdinalIgnoreCase);
        var matchesState = SelectedStateFilter == "All states"
            || string.Equals(environment.State, SelectedStateFilter, StringComparison.OrdinalIgnoreCase);
        var matchesRegion = SelectedRegionFilter == "All regions"
            || string.Equals(environment.Region, SelectedRegionFilter, StringComparison.OrdinalIgnoreCase);
        var matchesSearch = string.IsNullOrWhiteSpace(SearchText)
            || environment.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || environment.FinOpsUrl?.ToString().Contains(SearchText, StringComparison.OrdinalIgnoreCase) == true;

        return matchesType && matchesState && matchesRegion && matchesSearch;
    }

    private bool TryValidateSelection(out string warning)
    {
        if (_session is null || SelectedSource is null || SelectedTarget is null)
        {
            warning = "Sign in and select both environments.";
            return false;
        }

        if (!SelectedSource.IsSelectableFinOps
            || !SelectedTarget.IsSelectableFinOps
            || !_finOpsHostPolicy.IsAllowed(SelectedSource.FinOpsUrl)
            || !_finOpsHostPolicy.IsAllowed(SelectedTarget.FinOpsUrl))
        {
            warning = "Both environments must come from discovery or be added explicitly in this tool, with valid HTTPS base URLs.";
            return false;
        }

        if (string.Equals(SelectedSource.ProviderEnvironmentId, SelectedTarget.ProviderEnvironmentId, StringComparison.OrdinalIgnoreCase))
        {
            warning = "Source and Target must be different environments.";
            return false;
        }

        if (!string.Equals(SelectedSource.TenantId, _session.TenantId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(SelectedTarget.TenantId, _session.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            warning = "Source, Target, and the signed-in account must belong to the same tenant.";
            return false;
        }

        if (!string.Equals(SelectedSource.TenantId, SelectedTarget.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            warning = "Source and Target must belong to the same tenant.";
            return false;
        }

        if (_sourceValidationStatus != ConnectionValidationStatus.Succeeded
            || _targetValidationStatus != ConnectionValidationStatus.Succeeded)
        {
            warning = "Test both FinOps connections successfully before saving.";
            return false;
        }

        warning = string.Empty;
        return true;
    }

    private bool HasDuplicateUrlWithDifferentId() =>
        SelectedSource is not null
        && SelectedTarget is not null
        && string.Equals(
            SelectedSource.FinOpsUrl?.GetLeftPart(UriPartial.Authority),
            SelectedTarget.FinOpsUrl?.GetLeftPart(UriPartial.Authority),
            StringComparison.OrdinalIgnoreCase)
        && !string.Equals(SelectedSource.ProviderEnvironmentId, SelectedTarget.ProviderEnvironmentId, StringComparison.OrdinalIgnoreCase);

    private void RestoreSelection(AppConfiguration configuration)
    {
        if (configuration.AuthenticationContext is not { } authenticationContext)
        {
            return;
        }

        if (_session is null || !string.Equals(_session.TenantId, authenticationContext.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            _loadedConfiguration = configuration with
            {
                SourceEnvironment = MarkForRevalidation(configuration.SourceEnvironment),
                TargetEnvironment = MarkForRevalidation(configuration.TargetEnvironment)
            };
            StatusMessage = "Saved environments belong to a different or signed-out account and require revalidation.";
            return;
        }

        SelectedSource = ToDiscovered(configuration.SourceEnvironment, authenticationContext.TenantId);
        SelectedTarget = ToDiscovered(configuration.TargetEnvironment, authenticationContext.TenantId);
        SourceValidationMessage = "Saved connection requires a new test.";
        TargetValidationMessage = "Saved connection requires a new test.";
        _sourceValidationStatus = ConnectionValidationStatus.RequiresRevalidation;
        _targetValidationStatus = ConnectionValidationStatus.RequiresRevalidation;
        CurrentStepIndex = 3;
    }

    private void InvalidateSelections()
    {
        SelectedSource = null;
        SelectedTarget = null;
        SourceValidationMessage = "Requires revalidation";
        TargetValidationMessage = "Requires revalidation";
        _sourceValidationStatus = ConnectionValidationStatus.RequiresRevalidation;
        _targetValidationStatus = ConnectionValidationStatus.RequiresRevalidation;

        if (_loadedConfiguration is not null)
        {
            _loadedConfiguration = _loadedConfiguration with
            {
                SourceEnvironment = MarkForRevalidation(_loadedConfiguration.SourceEnvironment),
                TargetEnvironment = MarkForRevalidation(_loadedConfiguration.TargetEnvironment)
            };
        }

        StatusMessage = "The tenant changed. Previously saved environments cannot be reused.";
    }

    private void UpdateSessionDisplay() =>
        SessionDisplay = _session is null
            ? "Not signed in"
            : $"{_session.Username}  |  Tenant {_session.TenantId}";

    private static EnvironmentConnection? MarkForRevalidation(EnvironmentConnection? environment) =>
        environment is null
            ? null
            : environment with { ValidationStatus = ConnectionValidationStatus.RequiresRevalidation };

    private static DiscoveredEnvironment? ToDiscovered(EnvironmentConnection? environment, string tenantId) =>
        environment is null
            ? null
            : new DiscoveredEnvironment
            {
                ProviderEnvironmentId = environment.ProviderEnvironmentId,
                TenantId = tenantId,
                DisplayName = environment.DisplayName,
                FinOpsEnvironmentId = environment.FinOpsEnvironmentId,
                FinOpsEnvironmentType = environment.FinOpsEnvironmentType,
                FinOpsUrl = environment.FinOpsUrl,
                PowerPlatformType = environment.PowerPlatformType,
                State = environment.State,
                Region = environment.Region
            };

    private static void AddSelectedIfMissing(
        List<DiscoveredEnvironment> items,
        DiscoveredEnvironment? selected,
        IFinOpsHostPolicy hostPolicy)
    {
        if (selected is not null
            && selected.IsSelectableFinOps
            && hostPolicy.IsAllowed(selected.FinOpsUrl)
            && items.All(item => item.ProviderEnvironmentId != selected.ProviderEnvironmentId))
        {
            items.Add(selected);
        }
    }

    private static bool NotBlank(string? value) => !string.IsNullOrWhiteSpace(value);

    private static string FormatSummary(DiscoveredEnvironment? environment, string validation) =>
        environment is null
            ? "Not selected"
            : $"{environment.DisplayName}\n{environment.FinOpsUrl}\n{validation}";

    private static string ToSafeMessage(Exception exception) => exception switch
    {
        Microsoft.Identity.Client.MsalException msalException =>
            $"Token acquisition failed ({msalException.ErrorCode}). Check the Dynamics ERP delegated permission, consent, and FO application-to-user mapping. The first FO token may require consent; later requests should use the signed-in account silently.",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } => "The Power Platform session expired or the token audience is invalid. Sign in again.",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } => "The signed-in account does not have permission to discover environments.",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } => "Power Platform throttled the request. Wait and try again.",
        HttpRequestException httpException when httpException.StatusCode.HasValue =>
            $"Request failed with HTTP {(int)httpException.StatusCode.Value} ({httpException.StatusCode.Value}).",
        HttpRequestException => "The service could not be reached. Check the network and try again.",
        InvalidOperationException => exception.Message,
        _ => "The operation failed. Check the local application log for details."
    };
}