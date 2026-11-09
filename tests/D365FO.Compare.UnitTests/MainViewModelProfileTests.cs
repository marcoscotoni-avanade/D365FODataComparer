using D365FO.Compare.Application.Authentication;
using D365FO.Compare.Application.Comparison;
using D365FO.Compare.Application.Configuration;
using D365FO.Compare.Application.DataComparison;
using D365FO.Compare.Application.EnvironmentDiscovery;
using D365FO.Compare.Application.Metadata;
using D365FO.Compare.Application.Validation;
using D365FO.Compare.Desktop.ViewModels;
using D365FO.Compare.Domain.Authentication;
using D365FO.Compare.Domain.Configuration;
using D365FO.Compare.Domain.Environments;
using D365FO.Compare.Domain.Metadata;
using D365FO.Compare.Infrastructure.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace D365FO.Compare.UnitTests;

public sealed class MainViewModelProfileTests
{
    [Fact]
    public async Task AddingAndActivatingProfilesKeepsTenantSessionsSeparate()
    {
        var authentication = new Authentication();
        var configuration = new ConfigurationRepository();
        var dataWorkspace = DataComparisonWorkspaceTests.Create(new ActiveComparisonContext());
        var metadataWorkspace = new MetadataWorkspaceViewModel(
            new MetadataProvider(),
            new SnapshotRepository(),
            new SchemaComparisonService(),
            new ActiveComparisonContext());
        var viewModel = new MainViewModel(
            authentication,
            new DiscoveryProvider(),
            configuration,
            new ConnectionValidationService(),
            new ApprovedFinOpsHostPolicy(),
            NullLogger<MainViewModel>.Instance,
            metadataWorkspace,
            dataWorkspace,
            new GeneralParametersViewModel(new PreferencesRepository()),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:ClientId"] = "cccccccc-dddd-eeee-ffff-000000000000"
            }).Build());

        try
        {
            await viewModel.InitializeAsync();
            viewModel.NewProfileName = "Contoso";
            viewModel.NewProfileTenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
            viewModel.NewProfileClientId = "11111111-2222-3333-4444-555555555555";
            await viewModel.AddProfileCommand.ExecuteAsync(null);

            var contoso = Assert.Single(viewModel.Profiles);
            Assert.Equal(contoso.Id, viewModel.ActiveProfile?.Id);
            Assert.Equal(contoso.TenantId, authentication.LastSessionTenant);
            Assert.Equal(contoso.ClientId, authentication.LastClientId);

            viewModel.NewProfileName = "Fabrikam";
            viewModel.NewProfileTenantId = "bbbbbbbb-cccc-dddd-eeee-ffffffffffff";
            viewModel.NewProfileClientId = "22222222-3333-4444-5555-666666666666";
            await viewModel.AddProfileCommand.ExecuteAsync(null);

            Assert.Equal(2, viewModel.Profiles.Count);
            var fabrikam = viewModel.ActiveProfile;
            Assert.NotNull(fabrikam);
            Assert.NotEqual(contoso.Id, fabrikam.Id);
            Assert.Equal("22222222-3333-4444-5555-666666666666", fabrikam.ClientId);
            Assert.Equal(fabrikam.TenantId, authentication.LastSessionTenant);
            Assert.Equal(fabrikam.ClientId, authentication.LastClientId);

            viewModel.SelectedProfile = contoso;
            await viewModel.ActivateProfileCommand.ExecuteAsync(null);

            Assert.Equal(contoso.Id, viewModel.ActiveProfile?.Id);
            Assert.Equal(contoso.TenantId, authentication.LastSessionTenant);
            Assert.Equal(contoso.Id, configuration.Configuration?.ActiveProfileId);

            viewModel.SelectedProfile = viewModel.Profiles.Single(profile => profile.Id == contoso.Id);
            viewModel.ProfileNameText = "Contoso production";
            viewModel.ProfileClientIdText = "11111111-2222-3333-4444-555555555555";
            await viewModel.RenameProfileCommand.ExecuteAsync(null);
            Assert.Equal("Contoso production", viewModel.ActiveProfile?.Name);

            viewModel.SelectedProfile = viewModel.Profiles.Single(profile => profile.Id == fabrikam.Id);
            await viewModel.DeleteProfileCommand.ExecuteAsync(null);
            Assert.Equal(2, viewModel.Profiles.Count);
            Assert.Contains("Confirm removal", viewModel.StatusMessage, StringComparison.Ordinal);

            viewModel.ConfirmProfileDeletion = true;
            await viewModel.DeleteProfileCommand.ExecuteAsync(null);
            Assert.Single(viewModel.Profiles);
            Assert.Equal(contoso.Id, viewModel.ActiveProfile?.Id);
        }
        finally
        {
            await dataWorkspace.CloseAsync();
        }
    }

    private sealed class Authentication : IAuthenticationService
    {
        public string? LastSessionTenant { get; private set; }
        public string? LastClientId { get; private set; }

        public Task<UserSession> SignInAsync(string tenantId, string clientId, IntPtr parentWindow, CancellationToken cancellationToken, string? preferredHomeAccountId = null) =>
            Task.FromResult(Session(tenantId));

        public Task<UserSession> ChangeAccountAsync(string tenantId, string clientId, IntPtr parentWindow, CancellationToken cancellationToken) =>
            Task.FromResult(Session(tenantId));

        public Task<string> GetAccessTokenAsync(string tenantId, IReadOnlyCollection<string> scopes, IntPtr parentWindow, CancellationToken cancellationToken) =>
            Task.FromResult("fake-token");

        public Task SignOutAsync(string tenantId, string clientId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<UserSession?> GetCurrentSessionAsync(string tenantId, string clientId, CancellationToken cancellationToken, string? preferredHomeAccountId = null)
        {
            LastClientId = clientId;
            return Task.FromResult<UserSession?>(Session(tenantId));
        }

        public Task<UserSession?> GetCurrentSessionAsync(string tenantId, CancellationToken cancellationToken, string? preferredHomeAccountId = null) =>
            Task.FromResult<UserSession?>(Session(tenantId));

        private UserSession Session(string tenantId)
        {
            LastSessionTenant = tenantId;
            return new UserSession(tenantId, $"user@{tenantId}.com", $"account-{tenantId}");
        }
    }

    private sealed class ConfigurationRepository : IConfigurationRepository
    {
        public AppConfiguration? Configuration { get; private set; }
        public Task<AppConfiguration?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Configuration);
        public Task SaveAsync(AppConfiguration configuration, CancellationToken cancellationToken)
        {
            Configuration = configuration;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(CancellationToken cancellationToken)
        {
            Configuration = null;
            return Task.CompletedTask;
        }
    }

    private sealed class PreferencesRepository : IGeneralPreferencesRepository
    {
        public Task<GeneralPreferences> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(GeneralPreferences.Default);
        public Task SaveAsync(GeneralPreferences preferences, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class DiscoveryProvider : IEnvironmentDiscoveryProvider
    {
        public Task<IReadOnlyList<DiscoveredEnvironment>> DiscoverAsync(UserSession session, IntPtr parentWindow, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DiscoveredEnvironment>>([]);
    }

    private sealed class ConnectionValidationService : IConnectionValidationService
    {
        public Task<ConnectionValidationResult> ValidateAsync(DiscoveredEnvironment environment, IntPtr parentWindow, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectionValidationResult(ConnectionValidationStatus.Succeeded, "validated", DateTimeOffset.UtcNow));
    }

    private sealed class MetadataProvider : IEntityMetadataProvider
    {
        public Task<SchemaSnapshot> CreateSnapshotAsync(EnvironmentConnection environment, IProgress<MetadataProgress>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class SnapshotRepository : ISchemaSnapshotRepository
    {
        public Task<SchemaSnapshot?> LoadAsync(EnvironmentConnection environment, string accountId, CancellationToken cancellationToken) =>
            Task.FromResult<SchemaSnapshot?>(null);
        public Task SaveAsync(SchemaSnapshot snapshot, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(EnvironmentConnection environment, string accountId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
